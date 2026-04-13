# Plan: Migrate Stillpoint-Software to Trunk-Based Development

## Process

This is an infrastructure/workflow migration, not a code change. Each task follows a verify-before-proceeding discipline: make the change, confirm CI still passes, then move on.

The plan uses a **pilot-first** approach: update shared workflows, update cookiecutter templates, then validate end-to-end on `hyperbee.migrations` before touching any other repo. This catches problems early on a repo we're actively watching.

## Rollback Protocol

Every step in this migration is reversible. Before deleting any `develop` branch, record its SHA.

| Action | Rollback | Risk |
|--------|----------|------|
| Shared workflow change | `git revert` the commit on `shared-workflows` | Low -- instant revert |
| Branch rename (master -> main) | `gh api` rename back to `master` | Low -- GitHub preserves redirects |
| Merge develop into main | `git revert` the merge commit on `main` | Low -- clean revert |
| Delete develop branch | `gh api repos/{owner}/{repo}/git/refs -f ref=refs/heads/develop -f sha={recorded_sha}` | Low -- recreate from recorded SHA |
| Workflow trigger updates | `git revert` the commit | Low -- instant revert |

**Critical rule:** Every task that deletes a `develop` branch must record the SHA in the task's completion notes before deletion. Format: `develop SHA: <hash>`.

**Escape hatch:** If the pilot (Phase 3) reveals a problem with the shared workflow changes, revert the `shared-workflows` commit. All repos continue working on their existing branch model with zero impact.

## Objective

**Goal:** Migrate all Stillpoint-Software repositories from GitFlow (`develop` + `main`) to trunk-based development (`main` only), simplifying the release process from 6 manual steps to 3.

**Success Criteria:**
- All 12 repos with `develop` branches have been merged to `main` and `develop` deleted
- Two repos using `master` (`hyperbee.core`, `hyperbee.rules`) renamed to `main`
- Shared workflows updated to remove `develop`-specific logic
- Cookiecutter templates produce trunk-based repos
- Release process documented: PR to main, Create Release, Publish
- All repo CI pipelines pass on `main` after migration

**Constraints:**
- No work-in-progress lost -- all `develop` content merged before deletion
- Open PRs retargeted before branch deletion
- Shared workflows must be updated first (they're consumed by all repos)
- Pilot repo validated before rolling out to remaining repos

## Current State

### Repos with work on develop (merge required)

| Repo | Ahead | Behind | Notes |
|------|-------|--------|-------|
| hyperbee.pipeline | 32 | 1 | Most diverged -- merge carefully |
| hyperbee.migrations | 18 | 0 | Clean merge (no behind) -- **pilot repo** |
| hyperbee.expressions | 3 | 4 | Bidirectional divergence |
| hyperbee.xs | 1 | 1 | 1 open PR targeting develop |
| hyperbee.core | 1 | 0 | Uses `master` not `main` |

### Repos where develop has no unique work (delete only)

| Repo | Behind | Notes |
|------|--------|-------|
| hyperbee.collections | 1 | |
| hyperbee.extensions.dependencyinjection | 2 | |
| hyperbee.resources | 2 | |
| hyperbee.templating | 1 | |
| hyperbee.json | 2 | |
| hyperbee.auditing | 3 | |
| hyperbee.rules | 6 | Uses `master` not `main` |

## Shared Workflow Analysis

Only `set_version.yml` has significant branch-specific logic. The `auto` mode routes:

```
develop       -> bump minor, add -alpha
hotfix/*      -> bump patch, add -alpha
main / vX.Y   -> stable (no suffix)
else          -> treated like develop (bump minor, -alpha)
```

The monotonic-increase retry loop also has `develop`-specific bump logic.

`determine_build_configuration.yml` and `run_tests.yml` check `main` vs not-main -- no changes needed.

All other shared workflows (`prepare_release`, `pack_and_publish`, `format`, `issue_branch`, `template_update`, `test_report`, `unlist_package`) are fully parameterized and branch-agnostic.

---

## Phase 1: Update Shared Workflows

**Goal:** Make shared workflows trunk-based-compatible without breaking existing repos that still have `develop`.

**Prerequisites:** None -- this is the starting point.

**Completion Criteria:** Shared workflows accept `main` as the primary branch for all versioning logic. Existing repos still work until migrated (the `else` fallback covers `develop` during the transition).

### Task 1.1: Update `set_version.yml` auto mode

**Description:** Modify the `auto` mode branch routing in `set_version.yml` to work for trunk-based repos. The key change: when running on `main`, produce a stable version (already works). Remove `develop` as a special case -- feature branches (the `else` path) already produce `-alpha`, which is correct for trunk-based pre-release builds if ever needed.

**Implementation strategy:**
- In the `auto` case block (lines 179-193): remove the `develop` case, keep `hotfix/*` and `main/vX.Y` cases, adjust the `else` fallback
- In the monotonic-increase retry loop (lines 271-289): remove `develop`-specific bump, use the `else` (main) path
- The `else` fallback already produces `-alpha` for unknown branches -- this is correct behavior for feature branches

**Completion Criteria:**
- [ ] `auto` mode on `main` produces stable versions (unchanged)
- [ ] `auto` mode on `hotfix/*` produces `-alpha` patch bumps (unchanged)
- [ ] `auto` mode on feature branches produces `-alpha` (existing `else` path)
- [ ] No remaining references to `develop` in `set_version.yml`
- [ ] Existing repos with `develop` still work (the `else` path covers them during migration)

**Status:** Not Started

### Task 1.2: Update shared-workflows README

**Description:** Update the README in `shared-workflows` to document the trunk-based model and the simplified release process.

**Completion Criteria:**
- [ ] README reflects trunk-based workflow
- [ ] Release process documented as 3 steps

**Status:** Not Started

---

## Phase 2: Update Cookiecutter Templates

**Goal:** Update templates so new repos are born trunk-based. Doing this before the pilot validates the "target state" definition.

**Prerequisites:** Phase 1 complete.

**Completion Criteria:** Cookiecutter templates produce repos with `main`-only workflow triggers and no `develop` branch setup.

### Task 2.1: Update `project.cookiecutter`

**Description:** Update the cookiecutter template to generate trunk-based workflow files.

**Implementation strategy:**
- Remove `develop` from workflow trigger templates
- Update issue-branch config template to use `main`
- Update any documentation templates that reference the GitFlow process

**Completion Criteria:**
- [ ] Generated repos have no `develop` references in workflows
- [ ] Generated issue-branch config routes to `main`
- [ ] Template tested by generating a sample repo

**Status:** Not Started

### Task 2.2: Update `hyperbee.cookiecutter`

**Description:** Same changes as Task 2.1 for the hyperbee-specific template.

**Completion Criteria:**
- [ ] Same criteria as Task 2.1

**Status:** Not Started

---

## Phase 3: Pilot -- Migrate `hyperbee.migrations`

**Goal:** Validate the full migration end-to-end on a single diverged repo before rolling out. This is the current working directory (18 commits ahead, 0 behind -- cleanest merge candidate).

**Prerequisites:** Phase 1 complete. Phase 2 complete (we know what "correct" looks like from the templates).

**Completion Criteria:** `hyperbee.migrations` is fully trunk-based, CI passes, release workflow tested.

**Go/No-Go gate:** After this phase, review results before proceeding. If CI breaks or the release workflow misbehaves, fix here or revert shared workflows before touching other repos.

### Task 3.1: Merge `develop` into `main`

**Description:** Merge the 18 commits from `develop` into `main`.

**Implementation strategy:**
- Record develop SHA before any changes
- Create PR: `main <- develop`
- Squash-merge (consistent with existing process)

**Completion Criteria:**
- [ ] develop SHA recorded
- [ ] PR created and merged to `main`
- [ ] No work lost -- verify commit history

**Status:** Not Started

### Task 3.2: Update workflow files

**Description:** Update this repo's workflow files to remove `develop` references.

Per-repo changes:
- `format.yml`: remove `develop` from branch triggers
- `run_tests.yml`: remove `develop` from branch triggers
- `create_test_report.yml`: remove `develop` from branch filters
- `.github/issue-branch.yml`: change default source branch to `main`
- `update-version.yml`: remove `develop` condition (if present)

**Implementation strategy:**
- Make changes on a feature branch, PR to `main`, squash-merge
- Compare against cookiecutter template output to verify consistency

**Completion Criteria:**
- [ ] All workflow files updated
- [ ] No remaining `develop` references in `.github/`
- [ ] CI passes on `main`

**Status:** Not Started

### Task 3.3: Delete `develop` and verify

**Description:** Delete the `develop` branch and run a full verification.

**Completion Criteria:**
- [ ] develop SHA recorded in completion notes
- [ ] `develop` branch deleted
- [ ] CI passes on `main`
- [ ] Test a feature branch PR workflow (create branch, PR to main, verify CI triggers)
- [ ] Test `Create Release` workflow on `main` (dry run if possible, or real release if ready)

**Status:** Not Started

### Task 3.4: Go/No-Go decision

**Description:** Review pilot results. Decide whether to proceed with rollout or fix issues first.

**Checklist:**
- [ ] CI passes on `main`
- [ ] Feature branch PR workflow works (tests run, format checks run)
- [ ] Release workflow produces correct version numbers
- [ ] No regressions observed
- [ ] **Decision recorded: proceed / fix / revert**

**Status:** Not Started

---

## Phase 4: Rename `master` to `main`

**Goal:** Normalize the two repos that use `master` as their default branch before migrating them.

**Prerequisites:** Phase 3 Go/No-Go passed.

**Completion Criteria:** `hyperbee.core` and `hyperbee.rules` use `main` as default branch.

### Task 4.1: Rename `hyperbee.core` default branch to `main`

**Description:** Rename `master` to `main` using GitHub's branch rename feature. This automatically updates the default branch, branch protection rules, and open PRs.

**Implementation strategy:**
- Use `gh api` to rename the default branch
- Verify CI triggers still work on the new branch name
- Update any workflow files in the repo that reference `master`

**Completion Criteria:**
- [ ] Default branch is `main`
- [ ] No remaining references to `master` in workflow files
- [ ] CI passes on `main`

**Status:** Not Started

### Task 4.2: Rename `hyperbee.rules` default branch to `main`

**Description:** Same as Task 4.1 for `hyperbee.rules`.

**Completion Criteria:**
- [ ] Default branch is `main`
- [ ] No remaining references to `master` in workflow files
- [ ] CI passes on `main`

**Status:** Not Started

---

## Phase 5: Rollout -- Migrate Remaining Repos

**Goal:** Migrate all remaining repos now that the pattern is proven.

**Prerequisites:** Phase 3 pilot passed. Phase 4 complete (master repos renamed).

**Completion Criteria:** All remaining repos have `develop` merged (where needed), workflow files updated, and `develop` deleted.

### Task 5.1: Migrate no-divergence repos (batch)

**Description:** For the 7 repos where develop has no unique work, update workflow files and delete `develop`.

Repos: `hyperbee.collections`, `hyperbee.extensions.dependencyinjection`, `hyperbee.resources`, `hyperbee.templating`, `hyperbee.json`, `hyperbee.auditing`, `hyperbee.rules`

**Implementation strategy per repo:**
1. Record develop SHA
2. Update workflow files on `main` (PR, squash-merge)
3. Delete `develop`
4. Verify CI passes

**Completion Criteria:**
- [ ] All 7 repos: develop SHA recorded
- [ ] All 7 repos: workflow files updated
- [ ] All 7 repos: `develop` branch deleted
- [ ] All 7 repos: CI passes on `main`

**Status:** Not Started

### Task 5.2: Migrate `hyperbee.core` (1 commit ahead)

**Description:** Merge `develop` into `main`, update workflow files, delete `develop`.

**Implementation strategy:**
- Record develop SHA
- Create PR: `main <- develop`
- Squash-merge
- Update workflow files on `main`
- Delete `develop`

**Completion Criteria:**
- [ ] develop SHA recorded
- [ ] `develop` merged to `main` -- no work lost
- [ ] Workflow files updated
- [ ] `develop` branch deleted
- [ ] CI passes on `main`

**Status:** Not Started

### Task 5.3: Migrate `hyperbee.xs` (1 ahead, 1 behind, 1 open PR)

**Description:** Retarget the open PR, merge `develop` into `main`, update workflow files, delete `develop`.

**Implementation strategy:**
- Record develop SHA
- Retarget the open PR base from `develop` to `main`
- Merge `develop` into `main` (PR, squash-merge)
- Update workflow files
- Delete `develop`

**Completion Criteria:**
- [ ] develop SHA recorded
- [ ] Open PR retargeted to `main`
- [ ] `develop` merged to `main`
- [ ] Workflow files updated
- [ ] `develop` branch deleted
- [ ] CI passes on `main`

**Status:** Not Started

### Task 5.4: Migrate `hyperbee.expressions` (3 ahead, 4 behind)

**Description:** Bidirectional divergence -- merge `main` into `develop` first to resolve conflicts, then merge `develop` into `main`.

**Implementation strategy:**
- Record develop SHA
- Merge `main` into `develop` locally to catch up
- Resolve any conflicts
- Create PR: `main <- develop`
- Squash-merge
- Update workflow files
- Delete `develop`

**Completion Criteria:**
- [ ] develop SHA recorded
- [ ] Conflicts resolved (if any)
- [ ] `develop` merged to `main`
- [ ] Workflow files updated
- [ ] `develop` branch deleted
- [ ] CI passes on `main`

**Status:** Not Started

### Task 5.5: Migrate `hyperbee.pipeline` (32 ahead, 1 behind)

**Description:** Most diverged repo. Merge `main` into `develop` first, then merge `develop` into `main`.

**Implementation strategy:**
- Record develop SHA
- Merge `main` into `develop` to pick up the 1 commit behind
- Resolve any conflicts
- Create PR: `main <- develop`
- Squash-merge
- Update workflow files
- Delete `develop`

**Completion Criteria:**
- [ ] develop SHA recorded
- [ ] `main` merged into `develop` first
- [ ] Conflicts resolved (if any)
- [ ] `develop` merged to `main`
- [ ] Workflow files updated
- [ ] `develop` branch deleted
- [ ] CI passes on `main`

**Status:** Not Started

---

## Phase 6: Documentation and Final Audit

**Goal:** Document the new release process and verify everything is clean.

**Prerequisites:** All previous phases complete.

**Completion Criteria:** Release process documented, no stale references to `develop` or GitFlow anywhere in the org.

### Task 6.1: Document the new release process

**Description:** Update or create documentation for the simplified release process.

New process:
1. Feature branches PR into `main` (squash-merge)
2. Run `Create Release` workflow on `main` (select bump type)
3. Publish draft release (triggers NuGet pack/publish)

**Completion Criteria:**
- [ ] Release process documented in shared-workflows README
- [ ] Any org-level documentation updated

**Status:** Not Started

### Task 6.2: Final audit

**Description:** Verify no remaining `develop` branches or references across the org.

**Completion Criteria:**
- [ ] No `develop` branches exist in any Stillpoint repo
- [ ] No `master` default branches remain (all renamed to `main`)
- [ ] All repos CI passes on `main`
- [ ] Grep shared-workflows for any remaining `develop` references

**Status:** Not Started

---

## Learnings Ledger

*Populated during execution.*

---

## Status Summary

| Phase | Status |
|-------|--------|
| Phase 1: Update Shared Workflows | Not Started |
| Phase 2: Update Cookiecutter Templates | Not Started |
| Phase 3: Pilot -- hyperbee.migrations | Not Started |
| Phase 4: Rename master to main | Not Started |
| Phase 5: Rollout -- Remaining Repos | Not Started |
| Phase 6: Documentation and Final Audit | Not Started |

**Current Task:** --
**Next Action:** Begin Phase 1, Task 1.1 -- update `set_version.yml`
**Blockers:** None
