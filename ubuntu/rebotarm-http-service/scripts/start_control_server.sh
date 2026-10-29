#!/usr/bin/env bash

set -euo pipefail
source "$(dirname "$0")/env.sh"
cd "${REBOTARM_ROOT}"

if [[ -f "${REBOTARM_ROOT}/config/control_server.env" ]]; then
  set -a
  source "${REBOTARM_ROOT}/config/control_server.env"
  set +a
fi

exec python3 "${REBOTARM_ROOT}/scripts/robot_control_server.py" "$@"
