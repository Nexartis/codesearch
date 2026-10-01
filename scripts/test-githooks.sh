#!/bin/bash
# test-githooks.sh — pins the branch-aware agent-file lifecycle in .githooks/.
#
# Runs against the REAL .githooks/ files on a throwaway fixture repo (no cargo,
# no network): sources lib/root-md-guard.sh directly for the allowlist cases
# and invokes .githooks/post-checkout with hook-style arguments for the
# lifecycle cases. Run from anywhere: bash scripts/test-githooks.sh

set -uo pipefail

REPO_ROOT=$(cd "$(dirname "$0")/.." && pwd)
HOOKS="$REPO_ROOT/.githooks"
FIXTURE=$(mktemp -d)
PASS=0
FAIL=0

cleanup() { rm -rf "$FIXTURE"; }
trap cleanup EXIT

ok()   { PASS=$((PASS + 1)); echo "  PASS: $1"; }
bad()  { FAIL=$((FAIL + 1)); echo "  FAIL: $1"; }

assert() { # assert <description> <expected-rc> <actual-rc>
    if [ "$2" = "$3" ]; then ok "$1"; else bad "$1 (expected rc=$2, got rc=$3)"; fi
}

run_guard() { # exit status of root_md_guard against the fixture's index
    ( . "$HOOKS/lib/root-md-guard.sh" && root_md_guard ) >/dev/null 2>&1
}

stage() { git add -- "$@" >/dev/null 2>&1; }
unstage_all() { git reset >/dev/null 2>&1; }

echo "== fixture =="
cd "$FIXTURE" || exit 1
# NB: cd + plain git (no `git -C`): native Windows git rejects MSYS /tmp paths
git init -q -b develop || { echo "git init failed"; exit 1; }
git config user.email t@t && git config user.name t
echo "# template" > AGENTS.develop.md
git add AGENTS.develop.md && git commit -qm init

echo "== guard: develop blocks agent files, allows template =="
echo plan > AGENTS.md && echo ptr > CLAUDE.md && echo diag > DIAGNOSE_x.md
stage AGENTS.md        ; assert "develop: AGENTS.md introduction blocked" 1 "$(run_guard; echo $?)"
stage CLAUDE.md        ; assert "develop: CLAUDE.md introduction blocked" 1 "$(run_guard; echo $?)"
stage DIAGNOSE_x.md    ; assert "develop: stray md blocked" 1 "$(run_guard; echo $?)"
unstage_all
echo more >> AGENTS.develop.md && stage AGENTS.develop.md
assert "develop: AGENTS.develop.md allowed" 0 "$(run_guard; echo $?)"
unstage_all

echo "== pre-merge-commit hook: executed, not just wired =="
stage AGENTS.md
bash "$HOOKS/pre-merge-commit" >/dev/null 2>&1
assert "pre-merge-commit blocks staged AGENTS.md on develop" 1 "$?"
unstage_all
bash "$HOOKS/pre-merge-commit" >/dev/null 2>&1
assert "pre-merge-commit passes with a clean index on develop" 0 "$?"

echo "== guard: feature branch allows agent files, blocks strays =="
git checkout -qb feature/x
stage AGENTS.md CLAUDE.md
assert "feature: AGENTS.md+CLAUDE.md allowed" 0 "$(run_guard; echo $?)"
stage DIAGNOSE_x.md    ; assert "feature: stray md blocked" 1 "$(run_guard; echo $?)"
unstage_all

echo "== post-checkout: feature branch creates files when absent =="
rm -f AGENTS.md CLAUDE.md
sh "$HOOKS/post-checkout" 0 0 1 >/dev/null
[ -f AGENTS.md ] && ok "feature checkout: AGENTS.md created" || bad "feature checkout: AGENTS.md created"
[ -f CLAUDE.md ] && ok "feature checkout: CLAUDE.md created" || bad "feature checkout: CLAUDE.md created"
[ "$(cat CLAUDE.md)" = "Read AGENTS.md." ] && ok "CLAUDE.md pointer content" || bad "CLAUDE.md pointer content"
printf 'workplan-marker\n' >> AGENTS.md

echo "== post-checkout: never overwrites an existing work plan =="
sh "$HOOKS/post-checkout" 0 0 1 >/dev/null
grep -q workplan-marker AGENTS.md && ok "existing AGENTS.md untouched" || bad "existing AGENTS.md untouched"

echo "== post-checkout: develop removes untracked leftovers =="
git checkout -q develop
[ -f AGENTS.md ] && ok "untracked AGENTS.md survived switch to develop (fixture premise)" \
                 || bad "untracked AGENTS.md survived switch to develop (fixture premise)"
sh "$HOOKS/post-checkout" 0 0 1 >/dev/null
[ ! -f AGENTS.md ] && ok "develop: untracked AGENTS.md removed" || bad "develop: untracked AGENTS.md removed"
[ ! -f CLAUDE.md ] && ok "develop: untracked CLAUDE.md removed" || bad "develop: untracked CLAUDE.md removed"

echo "== post-checkout: detached HEAD is a no-op =="
git checkout -q --detach HEAD
sh "$HOOKS/post-checkout" 0 0 1 >/dev/null
[ ! -f AGENTS.md ] && [ ! -f CLAUDE.md ] && ok "detached: no files created" || bad "detached: no files created"

echo "== wiring: pre-merge-commit gate exists and delegates to the guard =="
[ -f "$HOOKS/pre-merge-commit" ] && ok "pre-merge-commit present" || bad "pre-merge-commit present"
grep -q "root-md-guard" "$HOOKS/pre-merge-commit" && ok "pre-merge-commit sources guard lib" || bad "pre-merge-commit sources guard lib"
grep -q "root-md-guard" "$HOOKS/pre-commit" && ok "pre-commit sources guard lib" || bad "pre-commit sources guard lib"
bash -n "$HOOKS/pre-commit" && bash -n "$HOOKS/pre-merge-commit" && bash -n "$HOOKS/lib/root-md-guard.sh" \
    && ok "hook syntax (bash -n)" || bad "hook syntax (bash -n)"

echo ""
echo "githooks tests: $PASS passed, $FAIL failed"
[ "$FAIL" = 0 ]
