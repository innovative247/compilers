# CLAUDE.md

This file provides guidance to Claude Code when working with code in this repository.

## Guidelines for Claude

1. You have full read/write access to @/claude_docs/ for all context (*.md) files, even temporary files you need in order to reason. Do not ask for permission to read/write to this location.
2. First think through the problem, read the codebase for relevant files, and write a plan to @/claude_docs/todo.md.
3. The plan should have a list of todo items that you can check off as you complete them
4. Before you begin working, check in with me and I will verify the plan.
5. Then, begin working on the todo items, marking them as complete as you go.
6. Please every step of the way just give me a high level explanation of what changes you made
7. Make every task and code change you do as simple as possible. We want to avoid making any massive or complex changes. Every change should impact as little code as possible. Everything is about simplicity.
8. Finally, add a review section to the  @/claude_docs/todo.md file with a summary of the changes you made and any other relevant information.
9. DO NOT BE LAZY. NEVER BE LAZY. IF THERE IS A BUG FIND THE ROOT CAUSE AND FIX IT. NO TEMPORARY FIXES. YOU ARE A SENIOR DEVELOPER. NEVER BE LAZY
10. MAKE ALL FIXES AND CODE CHANGES AS SIMPLE AS HUMANLY POSSIBLE. THEY SHOULD ONLY IMPACT NECESSARY CODE RELEVANT TO THE TASK AND NOTHING ELSE. IT SHOULD IMPACT AS LITTLE CODE AS POSSIBLE. YOUR GOAL IS TO NOT INTRODUCE ANY BUGS. IT'S ALL ABOUT SIMPLICITY
11. Do NOT create excessive documentation files. Only create files when absolutely necessary to move the project forward. Session summaries, completion announcements, checklists, and debug logs should NOT be saved as separate .md files.
12. Do NOT create setup guides, completion summaries, F5 documentation, test checklists, fix summaries, or any other temporary documentation. If information is important for the lifetime of the project, update @/claude_docs/PROJECT-STATUS.md instead.

## Cross-Platform Requirements

These Python compilers must work on **Windows, Mac, and Ubuntu**. Do not implement solutions that depend on platform-specific tools (e.g., Microsoft bcp.exe, Windows-only utilities).

Key cross-platform considerations:
- Use FreeTDS (freebcp, tsql) for database operations - available on all platforms
- Use pyodbc with appropriate ODBC drivers for each platform
- Be aware of Windows-specific file handling issues (e.g., Ctrl-Z/0x1a as EOF in text mode)
