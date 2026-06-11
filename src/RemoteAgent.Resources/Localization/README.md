# Localization / Fordítás

User-facing strings live here as .NET resource files (`.resx`).

- `Strings.resx` — **English** (the base / fallback language).
- `Strings.hu.resx` — Hungarian.
- `Strings.<culture>.resx` — any other language (e.g. `Strings.de.resx`, `Strings.fr.resx`).

## Adding a new language (translator heroes welcome 🦸)

1. Copy `Strings.resx` to `Strings.<culture>.resx`, where `<culture>` is the
   two-letter code (`de`, `fr`, `it`, ...) or a specific one (`pt-BR`).
2. Translate **only the `<value>` text** inside each `<data>` block. Keep the
   `name` attributes and any `{0}`, `{1}` placeholders exactly as they are.
3. Build — the SDK picks it up automatically (no code or `.csproj` changes).

## Notes

- The English base is `Strings.resx` (not `en.resx`): this is the .NET satellite
  convention — the file without a culture suffix is the fallback, and each
  translation is `Strings.<culture>.resx`. This is what lets a new language be a
  pure drop-in.
- Strings are accessed via the typed `Strings` class. When you add a **new key**,
  add it to `Strings.resx` first (English), then to each translation, and add a
  matching property in `Strings.cs`.
