#!/usr/bin/env bash
# Launcher for the Linux/X11 port of Precision Jump.
# Uses the system Python so GTK3 (python3-gobject) and cairo are available.
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"
exec /usr/bin/python3 -m precision_jump.app "$@"
