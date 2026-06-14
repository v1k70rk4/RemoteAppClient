# Localization

Runtime strings for `RemoteAgent` are collected here.

- `String.hu.cs` contains the Hungarian translation dictionary.
- `String.en.cs` contains the English fallback translation dictionary.
- `Strings.cs` owns language selection, fallback lookup, and typed `L.SomeKey` properties used by code.

Translators should edit values only, for example the right side of `[nameof(SomeKey)] = "..."`. Keep keys, placeholders such as `{0}`, and escape sequences such as `\n` intact.

Fallback order is: selected language -> English -> key name. This keeps the app running even if a translated key is missing.
