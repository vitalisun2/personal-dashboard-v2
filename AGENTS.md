# Agent instructions

## Required completion workflow

Before considering a task complete, every agent must:

1. Perform a brief, focused review of its changes for obvious mistakes, unintended edits, and regressions.
2. Run the applicable basic checks for compilation errors and build the affected project or solution. If a check cannot be run or does not apply, state that clearly in the handoff.
3. Fix any issues found and repeat the relevant review and checks.
4. Commit the task-owned changes and push the commit to the task's intended remote branch.

Keep the review and checks proportional to the change, and include their results and the commit/push status in the final handoff. Never include unrelated pre-existing work in the commit.
