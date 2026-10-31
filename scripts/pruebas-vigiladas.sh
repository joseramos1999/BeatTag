#!/usr/bin/env bash
#
# Corre las pruebas con un vigilante y publica lo que falle como UNA anotación del run. Lo usan la
# CI y la publicación de una versión (ci.yml y release.yml), desde EtiquetadorNet y con la solución
# ya compilada en Release.
#
#   ../scripts/pruebas-vigiladas.sh          todas las pruebas
#   ../scripts/pruebas-vigiladas.sh ABC      solo las de las clases que empiezan por A, B o C
#
# EN macOS, EN SU PROPIA SESIÓN. Desde el 20 de octubre de 2026, uno de cada ocho equipos macOS de
# GitHub dejaba de responder a los pocos segundos de empezar las pruebas y a los 46 minutos se daba
# por perdido, sin ningún registro. No era el equipo: un latido independiente del runner mostró que
# morían a la vez Runner.Listener, Runner.Worker y las pruebas, que en GitHub comparten grupo de
# procesos. Un proceso parado (SIGSTOP, lo hacía Process.Kill(entireProcessTree: true) de .NET al
# cancelar un programa) en un grupo huérfano hace que el núcleo mande SIGHUP a todo el grupo. La
# aplicación ya no para a nadie (Procesos.Matar), y además las pruebas corren aquí en una sesión
# propia: si algo vuelve a hacerlo, el SIGHUP se lleva las pruebas, que fallan y lo dicen, y no el
# runner. Ver los detalles en EtiquetadorNet/Etiquetador.Core/Procesos.cs.
#
# EL VIGILANTE, también solo en macOS, escribe cada segundo cuántas pruebas van, cuáles están en
# marcha y cómo están la memoria (la que dice macOS, contando la comprimida), el swap, el disco y
# los procesos. Mientras GitHub tiene un trabajo «en marcha», su página enseña esa salida aunque el
# equipo ya no responda. Si algo se dispara, corta las pruebas antes de que el equipo caiga y
# publica qué estaba en marcha. .NET tiene además un tope de memoria: una prueba que se desboque
# falla ella, con su pila. Picos medidos en octubre de 2026: 400-600 MB de las pruebas y nunca menos
# de un 79 % de memoria libre.

set -u

filtro=""
iniciales="${1:-}"
for ((i = 0; i < ${#iniciales}; i++)); do
  filtro="${filtro:+$filtro|}FullyQualifiedName~Etiquetador.Tests.${iniciales:i:1}"
done

es_mac=false
if [ "$(uname -s)" = Darwin ]; then es_mac=true; fi

# --blame-hang-timeout: una prueba que se queda colgada se corta a los 10 minutos y se dice cuál era.
# xUnit avisa además de las que pasan de 20 s.
orden=(dotnet test -c Release --no-build --verbosity normal --blame-hang-timeout 10m --blame-hang-dump-type none)
if [ -n "$filtro" ]; then orden+=(--filter "$filtro"); fi
orden+=(-- xUnit.LongRunningTestSeconds=20 xUnit.DiagnosticMessages=true)
if $es_mac; then
  export DOTNET_GCHeapHardLimit=0x100000000   # 4 GB por proceso; el equipo tiene 7
  # El registro de diagnóstico de vstest apunta cuándo empieza y acaba cada prueba: con él, el
  # vigilante dice cuáles están en marcha en cada momento.
  rm -rf diag
  orden=(nice -n 10 "${orden[@]:0:2}" --diag diag/vstest.txt "${orden[@]:2}")
  # Sesión y grupo de procesos propios (setsid; macOS no trae la orden, Python sí).
  if command -v python3 > /dev/null; then
    orden=(python3 -c 'import os, sys; os.setsid(); os.execvp(sys.argv[1], sys.argv[1:])' "${orden[@]}")
  fi
fi

"${orden[@]}" > test.log 2>&1 &
pid=$!

# --- Lo que mira el vigilante (solo en macOS)
memoria_libre() { sysctl -n kern.memorystatus_level 2>/dev/null; }   # % libre, contando la comprimida
swap_mb() {
  sysctl -n vm.swapusage 2>/dev/null | awk '{ for (i = 1; i < NF - 1; i++) if ($i == "used") {
    v = $(i + 2); m = (v ~ /G$/) ? 1024 : 1; sub(/[MG]$/, "", v); print int(v * m); exit } }'
}
disco_mb() { df -Pk "${TMPDIR:-/tmp}" 2>/dev/null | awk 'NR == 2 { print int($4 / 1024) }'; }
pruebas_mb() { ps -axo rss=,command= | awk '/testhost|dotnet test|vstest/ && !/awk/ { s += $1 } END { print int(s / 1024) }'; }
procesos() { ps -axo ppid= | grep -vc '^ *1$' || true; }

# Cuántas pruebas han terminado y cuál ha sido la última, en inglés (GitHub) o en español.
resultado='^  (Passed|Failed|Skipped|Correctas|Con error|Omitida) '
hechas() { local n; n=$(grep -cE "$resultado" test.log 2>/dev/null); echo "${n:-0}"; }
ultima() { grep -E "$resultado" test.log 2>/dev/null | tail -1 | sed -E "s/$resultado//; s/ \[[^]]*\]\$//" | cut -c1-110; }

# Las que han empezado y aún no han acabado, según el registro de diagnóstico del proceso de pruebas.
en_marcha_ahora() {
  local f; f=$(ls diag/*host*.txt 2>/dev/null | head -1); [ -n "$f" ] || return 0
  awk '/RecordStart: Starting test: / { sub(/.*Starting test: /, ""); sub(/\.\r?$/, ""); n[$0]++ }
       /RecordEnd: test: /           { sub(/.*RecordEnd: test: /, ""); sub(/ execution completed\.\r?$/, ""); n[$0]-- }
       END { for (t in n) if (n[t] > 0) { sub(/^Etiquetador\.Tests\./, "", t); printf "%s%s", s, t; s = ", " } }' "$f" | cut -c1-600
}

motivo=""
inicio=$(date +%s)
swap0=""
pico_mb=0; min_libre=100; pico_swap=0; min_disco=""; pico_procs=0
while kill -0 "$pid" 2>/dev/null; do
  sleep 1
  $es_mac || continue

  mb=$(pruebas_mb); libre=$(memoria_libre); swap=$(swap_mb); disco=$(disco_mb); procs=$(procesos)
  mb=${mb:-0}; libre=${libre:-100}; swap=${swap:-0}; disco=${disco:-999999}; procs=${procs:-0}
  if [ -z "$swap0" ]; then swap0=$swap; fi
  extra=$((swap - swap0))

  if [ "$mb" -gt "$pico_mb" ]; then pico_mb=$mb; fi
  if [ "$libre" -lt "$min_libre" ]; then min_libre=$libre; fi
  if [ "$extra" -gt "$pico_swap" ]; then pico_swap=$extra; fi
  if [ -z "$min_disco" ] || [ "$disco" -lt "$min_disco" ]; then min_disco=$disco; fi
  if [ "$procs" -gt "$pico_procs" ]; then pico_procs=$procs; fi
  linea="$(( $(date +%s) - inicio )) s · pruebas ${mb} MB · libre ${libre} % · swap +${extra} MB · disco ${disco} MB · ${procs} procesos"
  echo "$linea" >> vigilancia.log
  # También en la salida del paso, que GitHub enseña en directo: si el equipo cae, mientras el
  # trabajo sigue «en marcha» su página aún enseña el último segundo antes de caer.
  echo "$linea · $(hechas) hechas · última: $(ultima) · en marcha: $(en_marcha_ahora)"

  if   [ "$libre" -lt 10 ];   then motivo="macOS dice que solo queda un ${libre} % de memoria libre"
  elif [ "$extra" -gt 1024 ]; then motivo="el swap ha crecido ${extra} MB"
  elif [ "$disco" -lt 1024 ];  then motivo="solo quedan ${disco} MB libres en el disco"
  elif [ "$procs" -gt 400 ];   then motivo="hay ${procs} procesos en marcha"
  elif [ "$mb" -gt 3500 ];     then motivo="las pruebas usan ${mb} MB de memoria"
  fi
  if [ -n "$motivo" ]; then
    {
      echo "== top (memoria de cada proceso, con la comprimida)"; top -l 1 -o mem -n 12 -stats pid,ppid,mem,cpu,threads,command | tail -13
      echo "== ps (más memoria residente)"; ps -axo pid,ppid,rss,%cpu,etime,command | sort -k3 -nr | head -12
      echo "== ps (procesos más repetidos)"; ps -axo comm= | sort | uniq -c | sort -nr | head -8
      echo "== vm_stat"; vm_stat | head -16
    } > instantanea.log 2>&1
    # Primero el proceso de las pruebas, que es el que libera la memoria. Con unos segundos de
    # margen, dotnet test aún dice qué pruebas se estaban ejecutando al cortarlo.
    pkill -f testhost || true
    for _ in 1 2 3 4 5; do kill -0 "$pid" 2>/dev/null && sleep 1; done
    pkill -f "dotnet test" || true; kill "$pid" 2>/dev/null || true
    break
  fi
done
codigo=0; wait "$pid" || codigo=$?

if $es_mac; then
  echo "::group::Vigilancia, segundo a segundo"; cat vigilancia.log 2>/dev/null; echo "::endgroup::"
fi
picos="Vigilancia (picos): pruebas ${pico_mb} MB · memoria libre mínima ${min_libre} % · swap +${pico_swap} MB · disco libre mínimo ${min_disco:-?} MB · ${pico_procs} procesos"

# %0A es el salto de línea dentro de un mensaje de anotación.
anotacion() { head -90 | awk '{printf "%s%%0A", $0}'; }
en_marcha() { grep -iE -A6 "were running|se estaban ejecutando|aborted|anul|OutOfMemory" test.log || true; }

if [ -n "$motivo" ]; then
  echo "::group::Salida completa"; cat test.log; echo "::endgroup::"
  cuerpo=$( { echo "Cortado por el vigilante: $motivo"; echo "$picos"
              echo "-- En marcha al cortar:"; en_marcha | tail -20
              echo "-- Pruebas largas (xUnit):"; { grep -i "long running" test.log || true; } | tail -15
              echo "-- Últimas líneas:"; tail -10 test.log
              echo "-- Vigilancia:"; tail -6 vigilancia.log; cat instantanea.log; } | anotacion) || true
  echo "::error title=Pruebas cortadas por el vigilante::$cuerpo"
  exit 1
fi

if [ "$codigo" -eq 0 ]; then
  tail -3 test.log
  if $es_mac; then echo "$picos"; fi
  exit 0
fi

# 129 = 128 + SIGHUP: lo que antes se llevaba el runner (ver arriba), ahora solo a las pruebas.
if [ "$codigo" -eq 129 ]; then
  echo "::group::Salida completa"; cat test.log; echo "::endgroup::"
  cuerpo=$( { echo "Las pruebas murieron por SIGHUP: un proceso se quedó parado (SIGSTOP) en su grupo de procesos."
              echo "¿Se ha vuelto a usar Process.Kill(entireProcessTree: true) en vez de Procesos.Matar?"
              echo "-- En marcha:"; en_marcha_ahora | tr ',' '\n' | sed 's/^ *//'
              echo "-- Últimas líneas:"; tail -10 test.log; } | anotacion) || true
  echo "::error title=Pruebas cortadas por SIGHUP::$cuerpo"
  exit 1
fi

echo "::group::Salida completa"; cat test.log; echo "::endgroup::"
# UNA sola anotación con todo dentro: GitHub solo conserva unas diez por trabajo y además
# deduplica, así que emitir una por fallo perdía justo lo que hace falta ver. Incluye las pruebas
# que estaban en marcha cuando --blame-hang-timeout cortó la ejecución, y la pila de una prueba que
# se quedó sin memoria por el tope de .NET.
fallos=$( { grep -E "^[[:space:]]*(Failed|Con error) [A-Za-z]" test.log; grep -iE -A6 "hang|long running" test.log; en_marcha; } \
          | sed 's/^[[:space:]]*//' | sort -u) || true
total=$(printf '%s\n' "$fallos" | grep -c . || true)
cuerpo=$( { printf '%s\n' "$fallos" | head -60; if $es_mac; then echo "$picos"; fi; } | anotacion) || true
echo "::error title=Tests que fallan ($total)::$cuerpo"
exit 1
