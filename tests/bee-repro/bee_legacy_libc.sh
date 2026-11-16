#!/bin/sh
# Diagnostic alternative: retain the canary and isolate an older runtime to Bee.
exec /compat/lib/x86_64-linux-gnu/ld-linux-x86-64.so.2 \
    --library-path /compat/lib/x86_64-linux-gnu \
    "$0.real" "$@"
