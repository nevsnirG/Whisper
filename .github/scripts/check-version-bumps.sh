#!/usr/bin/env bash
# Fails when the source of a package changed between two commits but its <Version> did not.
# Packages are pushed with --skip-duplicate, so a change without a version bump is silently never published.
#
# Usage: check-version-bumps.sh <base-commit> [<head-commit>]
set -euo pipefail

base="$1"
head="${2:-HEAD}"

version_of() {
  sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' | tr -d '\r' | head -n 1
}

failed=0
for project in src/*/*.csproj; do
  directory="$(dirname "$project")"

  if git diff --quiet "$base" "$head" -- "$directory"; then
    continue
  fi

  # A project that does not exist at the base commit is new, and so is its version.
  if ! previous="$(git show "$base:$project" 2>/dev/null)"; then
    continue
  fi

  old_version="$(version_of <<<"$previous")"
  new_version="$(git show "$head:$project" | version_of)"

  if [ "$old_version" = "$new_version" ]; then
    echo "::error file=$project::$directory changed, but its <Version> is still $new_version. Without a bump the change is never published."
    failed=1
  fi
done

exit "$failed"
