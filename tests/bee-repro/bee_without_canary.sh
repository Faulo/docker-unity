#!/bin/sh
# Experimental workaround; install only in a disposable copy of a legacy editor.
for argument in "$@"; do
    shift
    if [ "$argument" != '--stdin-canary' ]; then
        set -- "$@" "$argument"
    fi
done
exec "$0.real" "$@"
