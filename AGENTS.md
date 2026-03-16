# Agent Instructions

## Project

Pooled TCP server library for .NET 10.0 built on `SocketAsyncEventArgs`. C# 14, nullable reference types enabled, SDK-style `.csproj`, xUnit v3 tests.

## Commands

```bash
dotnet build
dotnet test
dotnet test --logger "console;verbosity=detailed"
```

## Design Rules

- Always copy user buffers on send and receive. Zero-copy is not allowed.
- Assign each client to the least-loaded ordering lane on connect.
- `Client` is a pooled, reusable internal object holding all connection state.
- `ClientHandle` is a lightweight public struct (client reference + generation) that exposes send, disconnect, and read-only state. Generation tracking prevents use-after-recycle.
- `ClientSnapshot` is an immutable record struct used in disconnect and error callbacks.
- Consumers implementing `ISupportsOrderingLaneCount` are validated against `TcpServerSettings.OrderingLaneCount` at construction time.

## Coding Standards

### Naming

- `PascalCase` for types, methods, properties, events, constants.
- `_camelCase` for private instance fields.
- `camelCase` for locals and parameters.
- Interfaces start with `I`.

### Rules

- File-scoped namespaces only. No block-scoped namespaces.
- Explicit types. No `var`.
- No `ImplicitUsings`. Write all `using` directives explicitly.
- Nullable reference types are enabled. Annotate correctly. Do not suppress with `!` unless unavoidable.
- XML doc comments (`/// <summary>`) on all public types and members.
- One type per file. File name matches type name.
- No new external NuGet dependencies without discussion.

## Off Limits

Do not read or modify:

- `bin/`, `obj/`, `nupkgs/`, `packages/` -- build output
- `.idea/` -- IDE metadata
- `.github/` -- CI/CD pipelines
- `.version` -- managed by release process
- `LICENSE.md` -- legal

Never commit secrets, API keys, tokens, or credentials.
