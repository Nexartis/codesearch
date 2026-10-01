# root-md-guard.sh — shared root-md allowlist guard (sourced, never executed).
# Used by pre-commit (direct commits, conflict resolutions) and
# pre-merge-commit (clean merges never invoke pre-commit).
#
# The repo root keeps only its sanctioned markdown files (AGENTS.develop.md,
# section "Root file hygiene (markdown)"). Anything else — diagnoses, plans,
# test scenarios, worklogs — belongs in .docs/ (gitignored, local-only).
#
# The allowlist is branch-aware: AGENTS.md and CLAUDE.md are branch-local
# working files, dropped at feature finalization — on develop/master they are
# NOT allowlisted, so introducing them there is rejected, clean merge or not.
#
# Only introductions are checked (added/copied/renamed). Modifying an already
# tracked stray is only possible after a deliberate --no-verify bypass — the
# file itself is the violation then, and review catches it. Dot-folders
# (.githooks/, .github/, .claude/, ...) are out of scope by construction: a
# root-level file cannot be inside one.
#
# bash (not sh): process substitution. macOS bash 3.2 compatible.

root_md_guard() {
    # git branch --show-current: empty on detached HEAD (unborn HEAD prints
    # the branch name — harmless feature-branch treatment). rev-parse
    # --abbrev-ref would print "HEAD" when detached — creating branch-local
    # files there would later block rebase/bisect checkouts of commits that
    # track them — and exits 128 on unborn HEAD, killing a set -e caller
    # mid-hook.
    BRANCH=$(git branch --show-current 2>/dev/null || true)
    case "$BRANCH" in
        develop|master)
            ALLOWED_ROOT_MD="AGENTS.develop.md README.md README_CSharp.md CHANGELOG.md RELEASING.md"
            ;;
        *)
            ALLOWED_ROOT_MD="AGENTS.md AGENTS.develop.md CLAUDE.md README.md README_CSharp.md CHANGELOG.md RELEASING.md"
            ;;
    esac

    while IFS= read -r staged; do
        # root level = no slash anywhere in the staged (post-rename) path
        case "$staged" in
            */*) continue ;;
        esac
        # tr, not ${var,,}: the parameter-expansion lowercase needs bash >= 4 and
        # stock macOS still ships bash 3.2, where it is a fatal "bad substitution"
        # that would block EVERY commit containing a root-level file.
        case "$(printf '%s' "$staged" | tr 'A-Z' 'a-z')" in
            *.md) ;;
            *) continue ;;
        esac
        for allowed in $ALLOWED_ROOT_MD; do
            [ "$staged" = "$allowed" ] && continue 2
        done
        echo ""
        echo "guard: BLOCKED — root-level '$staged' is not on the md allowlist for branch '${BRANCH:-detached}'."
        echo "  Root markdown is limited to: $ALLOWED_ROOT_MD"
        echo "  AGENTS.md/CLAUDE.md are branch-local: feature branches only, dropped at finalization."
        echo "  Diagnoses, plans, worklogs and test scenarios belong in .docs/ (gitignored)."
        echo "  See AGENTS.develop.md, section \"Root file hygiene (markdown)\"."
        echo "  Deliberate? Use: git commit --no-verify / git merge --no-verify"
        echo ""
        return 1
# core.quotePath=false: with the default (true), git C-quotes non-ASCII paths
# (DIAGNOSE_ü.md -> "DIAGNOSE_\303\274.md"), and the quoted trailing `"` makes
# the *.md pattern miss — a stray with a non-ASCII name would sail through.
    done < <(git -c core.quotePath=false diff --cached --name-only --diff-filter=ACR)
    return 0
}
