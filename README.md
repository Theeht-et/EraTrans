# EraTrans

A lightweight Windows desktop tool for translating ERA games.

## Goals

- Reuse the existing EraBasic extraction and import logic where practical.
- Provide a simple WPF interface for dictionary extraction, editing, AI translation, and game import.
- Translate text entries only; variable entries must remain unchanged.
- Keep the original game files safe by working with a copy by default.

## Technology

- C# / WPF
- .NET 10 LTS
- GitHub Actions for build and test

The first development milestone is a minimal desktop application shell and a working CI build. Existing parser code will be integrated only after its dependencies and compatibility have been checked.
