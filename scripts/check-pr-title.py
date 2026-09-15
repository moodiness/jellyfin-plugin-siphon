#!/usr/bin/env python3
"""Validate Conventional Commit pull request titles."""
import os
import re

title = os.environ["PR_TITLE"]
if not re.fullmatch(r"(?:feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(?:\([a-z0-9._/-]+\))?!?: .+", title):
    raise SystemExit("Use a semantic PR title, for example: feat(catalog): integrate Stremio addons")
print("Semantic PR title verified")
