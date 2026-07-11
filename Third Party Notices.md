# Third Party Notices

PersistenceKit is distributed under the MIT License (see LICENSE.md). It relies on the
following third-party components, which are **not** redistributed with this package —
they are resolved by the Unity Package Manager or installed separately by the consumer.

## Newtonsoft.Json (required dependency)

- Package: `com.unity.nuget.newtonsoft-json`
- Copyright © James Newton-King
- License: MIT — https://github.com/JamesNK/Newtonsoft.Json/blob/master/LICENSE.md

Used by `NewtonsoftJsonHandler` for JSON serialization. Declared as a package dependency;
UPM installs it automatically.

## Optional integrations (only when present in the project)

These are detected via version-defines and are never bundled:

- **Odin Inspector** (Sirenix) — optional inspector rendering. Commercial license held by the user.
- **VContainer** (MIT) and **Zenject / Extenject** (MIT) — optional dependency-injection adapters.
- **System.Text.Json** — optional serializer backend, enabled via the `PERSISTENCEKIT_STJ` define.

No third-party source code is included in this repository.
