# Contributing to Session Provisioning

Issues and pull requests are welcome!

## Getting Started

1. Fork the repository
2. Create a feature branch from `main`
3. Make your changes
4. Submit a pull request

## Building

```bash
dotnet build --configuration Release
```

The plugin targets Jellyfin 10.11.11 and `net9.0`. Package references must remain
aligned with the supported Jellyfin server version.

## Testing

```bash
dotnet test
```

Changes that affect authorization, the provisioning-secret gate, or session creation
must also be tested against a disposable Jellyfin server:

```bash
scripts/smoke-test.sh
```

The smoke test requires Docker and must never be pointed at a production server.

## Linting

Run lint checks locally before opening a PR:

```bash
dotnet format whitespace --verify-no-changes
dotnet format style --verify-no-changes --severity warn
```

## Before changing behaviour

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and
[docs/SECURITY.md](docs/SECURITY.md) first. This is a deliberately small,
security-sensitive plugin, and the invariants in those documents are requirements.

Keep the plugin stateless and narrowly scoped. Do not add configuration, a dashboard
page, custom token persistence, duplicate authorization rules, or unrelated client
installer functionality.

## Reporting Issues

- Search existing issues before opening a new one
- Include the Jellyfin version, plugin version, and relevant logs
- Never include API keys, provisioning secrets, or access tokens
- Scrub Jellyfin logs before attaching them, because revoked tokens may appear in
  upstream logout messages

## Rules

- Keep branches, commits, and PRs focused. Do not mix unrelated local changes into the same PR.
- Use semantic names by default.

## Naming

- Branches: `fix/<scope>-<summary>`, `feat/<scope>-<summary>`, `refactor/<scope>-<summary>`
- Commits: `fix(scope): summary`, `feat(scope): summary`, `refactor(scope): summary`
- PR titles: `fix(scope): summary`, `feat(scope): summary`, `refactor(scope): summary`

## Pull Requests

- Keep changes focused and minimal
- Run the relevant validation before submitting
- Test against a running Jellyfin instance when changing runtime behavior
- Describe what your PR changes and why

## LLM Disclosure

This project uses LLM-assisted development. Contributions generated with AI assistance
are welcome, but please review and test all code before submitting.
