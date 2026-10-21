# Third-party notices

CaseKeeper is built with these components. Each one keeps its own licence.

| Component | Licence | Use |
|---|---|---|
| [.NET 8 runtime](https://github.com/dotnet/runtime) and WPF | MIT | Shipped with the app (self-contained) |
| [DocumentFormat.OpenXml](https://github.com/dotnet/Open-XML-SDK) 3.1.0 | MIT | Word (.docx) export and filling Word templates |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) 8.0.10 | MIT | Database access |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) bundle_e_sqlcipher 2.1.10 | Apache-2.0 | Native SQLite with SQLCipher encryption |
| [SQLCipher](https://github.com/sqlcipher/sqlcipher) (community edition, bundled by SQLitePCLRaw) | BSD-style (Zetetic LLC) | Encrypting the database |
| [PDFsharp](https://github.com/empira/PDFsharp) (PDFsharp-WPF) 6.1.1 | MIT | PDF output: bundles, letters and reports |
| [SharpZipLib](https://github.com/icsharpcode/SharpZipLib) 1.4.2 | MIT | Backups and archives |
| System.Security.Cryptography.ProtectedData 8.0.0 | MIT | Windows DPAPI, for the stored AI API key |

Optional, not bundled: [Ollama](https://ollama.com) (MIT) for the local AI features, if you install it yourself.

SQLitePCLRaw is licensed under the Apache License, Version 2.0 (https://www.apache.org/licenses/LICENSE-2.0).
SQLCipher: Copyright (c) 2008-2024 Zetetic LLC. All rights reserved. Redistribution and use in source and binary forms,
with or without modification, are permitted under the terms of its BSD-style licence
(https://www.zetetic.net/sqlcipher/license/).
