#!/usr/bin/env bash
#
# Corre las pruebas con un vigilante y publica lo que falle como UNA anotación del run. Lo usan la
# CI y la publicación de una versión (ci.yml y release.yml), desde EtiquetadorNet y con la solución
# ya compilada en Release.
#
#   ../scripts/pruebas-vigiladas.sh          todas las pruebas
#   ../scripts/pruebas-vigiladas.sh ABC      solo las de las clases que empiezan por A, B o C
#
# POR QUÉ EL VIGILANTE. Desde octubre de 2026, el equipo macOS de GitHub se queda de vez en cuando
# sin responder en mitad de las pruebas -en main, en los PR y en la Release, casi la mitad de las
# veces y sin relación con lo que cambiaba cada uno- y a los 46 minutos GitHub lo da por perdido sin
# dejar ningún registro. Ni el límite de 25 minutos del paso ni el vigilante anterior llegaron a
# actuar nunca: se para el equipo entero, no solo las pruebas. Aquel vigilante miraba cada 5 s la
# memoria residente de las pruebas, y en macOS la memoria comprimida no cuenta como residente, así
# que un desbordamiento podía no verse.
#
# Este mira cada segundo lo que dice el propio macOS -la memoria libre, que sí cuenta la comprimida,
# el swap y el disco- y el número de procesos, y deja las pruebas con menos prioridad que él para
# que no lo ahoguen. Si algo se dispara, corta las pruebas ANTES de que el equipo caiga y publica qué
# estaba en marcha. Además, .NET tiene un tope de memoria: una prueba que se desboque falla ella,
# con su pila, en vez de llevarse el equipo por delante.

set -u

filtro=""
iniciales="${1:-}"
for ((i = 0; i < ${#iniciales}; i++)); do
  filtro="${filtro:+$filtro|}FullyQualifiedName~Etiquetador.Tests.${iniciales:i:1}"
done
# Para diagnosticar: un filtro de dotnet test entero, que sustituye al de las iniciales.
if [ -n "${FILTRO_PRUEBAS:-}" ]; then filtro="$FILTRO_PRUEBAS"; fi

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
