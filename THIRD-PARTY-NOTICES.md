# Third-party software

Direct runtime dependencies are the official ModelContextProtocol SDK (Apache-2.0), MailKit and MimeKit (MIT), and Microsoft ASP.NET Core authentication components (MIT). MailKit transitively includes BouncyCastle.Cryptography (MIT). Exact versions and transitive graphs are recorded in each project's `packages.lock.json`.

Test-only dependencies include xUnit and Microsoft.NET.Test.Sdk and their transitive components. .NET standalone distributions include Microsoft runtime components under their own license terms. Containers also include their base-image operating system software. Retain those suppliers' license files in redistributed packages/images.

This file identifies dependencies; it does not replace their license texts. Release packaging includes machine-readable dependency information and copies bundled package license/notice files. Inspect the release artifact before public distribution. Development probes are not included in the product binary.
