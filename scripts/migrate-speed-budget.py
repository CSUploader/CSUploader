# Copyright (c) CSUploader. All rights reserved.
# Licensed under the MIT license. See LICENSE file in the project root for full license information.
#
# One-shot migration for docs/superpowers/plans/2026-08-23-shared-speed-limit-budget.md, Task 5:
# the pipeline carries a shared SpeedBudget instead of a per-stream Func<long?> rate delegate.
#
# The EXCLUDE set is load-bearing. Without it this script rewrites SpeedLimiter's own
# `Func<long?> getBytesPerSecond` parameter into `SpeedBudget speedBudget` and its calls into
# `speedBudget()`, and the tree stops compiling.
#
# Run once from the repo root:  python scripts/migrate-speed-budget.py

import pathlib
import re

EXCLUDE = {
    'SpeedLimiter.cs', 'SpeedBudget.cs', 'SpeedReservation.cs', 'SpeedLimitScopes.cs',
    'SpeedLimiterTests.cs', 'SpeedBudgetTests.cs', 'SpeedLimiterScopeTests.cs',
    'SpeedLimitTestFactory.cs', 'ManualTimeProvider.cs', 'ThrottledStream.cs',
    'ThrottledStreamConcurrencyTests.cs',
}

# Ordered. The first group renames the plumbing; the SECOND group retypes `Func<long?>` nested
# inside composite delegate types (the ~97 test-override signatures), which the first cannot match
# because the name does not follow the type there. The real migration needed both passes; a script
# with only the first leaves 73 files uncompilable.
SUBS = [
    (r'getBytesPerSecond:\s*ctx\.SpeedLimitProvider', 'speedBudget: ctx.SpeedBudget'),
    (r'ctx\.SpeedLimitProvider', 'ctx.SpeedBudget'),
    (r'SpeedLimitProvider\s*=\s*\(\)\s*=>\s*null', 'SpeedBudget = SpeedBudget.Unlimited'),
    (r'SpeedLimitProvider', 'SpeedBudget'),
    (r'Func<long\?>\?\s+getBytesPerSecond', 'SpeedBudget? speedBudget'),
    (r'Func<long\?>\s+getBytesPerSecond', 'SpeedBudget speedBudget'),
    (r'getBytesPerSecond', 'speedBudget'),

    # Second pass: the plumbing properties, then composite delegate types.
    (r'public required Func<long\?> SpeedBudget', 'public required SpeedBudget SpeedBudget'),
    (r'Func<long\?>\?', 'SpeedBudget?'),
    (r'Func<long\?>', 'SpeedBudget'),
]

changed = 0
for path in list(pathlib.Path('src').rglob('*.cs')) + list(pathlib.Path('tests').rglob('*.cs')):
    if any(part in ('bin', 'obj') for part in path.parts) or path.name in EXCLUDE:
        continue

    text = original = path.read_text(encoding='utf-8')
    for pattern, replacement in SUBS:
        text = re.sub(pattern, replacement, text)

    if text != original:
        path.write_text(text, encoding='utf-8', newline='\r\n')
        changed += 1

print(f'rewrote {changed} files')
