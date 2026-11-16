#!/bin/sh
# /source is the shared editor volume (read-only); /candidate must be disposable.
set -eu
version=2021.3.45f2
source_editor=/source/Hub/Editor/$version
candidate_editor=/candidate/Hub/Editor/$version
test -f "$source_editor/Editor/Data/bee_backend"
test ! -e "$source_editor/Editor/Data/bee_backend.real"
test ! -e "$candidate_editor"
mkdir -p /candidate/Hub/Editor
cp -a "$source_editor" "$candidate_editor"
mv "$candidate_editor/Editor/Data/bee_backend" "$candidate_editor/Editor/Data/bee_backend.real"
cp /repro/bee_without_canary.sh "$candidate_editor/Editor/Data/bee_backend"
chmod 755 "$candidate_editor/Editor/Data/bee_backend"
sha256sum "$source_editor/Editor/Data/bee_backend" "$candidate_editor/Editor/Data/bee_backend.real"
