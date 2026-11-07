# LinKit Workspace Agent Rules & Guidelines

## 1. Documentation Synchronization Requirement (MANDATORY)

Whenever you make changes to any feature, API, attribute, source generator behavior, or usage pattern in the `LinKit` repository, you **MUST** update both:
1. The local package `README.md` inside `E:\Workspace\My\LinKit\LinKit\<PackageName>\README.md` (if applicable).
2. The personal documentation website in `E:\Workspace\My\quoclinh-web`:
   - Path: `E:\Workspace\My\quoclinh-web\src\Host\QuocLinh.Web.Host\wwwroot\content\docs\linkit\`
   - Always update **both** English (`*.en.md`) and Vietnamese (`*.vi.md`) documentation files corresponding to the changed package:
     - `LinKit.Core` / `LinKit.Generator` -> `core.en.md` & `core.vi.md`
     - `LinKit.Data` / `LinKit.Data.Generator` -> `data.en.md` & `data.vi.md`
     - `LinKit.Grpc` / `LinKit.Grpc.Generator` -> `grpc.en.md` & `grpc.vi.md`
     - `LinKit.Caching` -> `caching.en.md` & `caching.vi.md`
     - `LinKit.Messaging.Kafka` -> `messaging-kafka.en.md` & `messaging-kafka.vi.md`
     - `LinKit.Messaging.RabbitMQ` -> `messaging-rabbitmq.en.md` & `messaging-rabbitmq.vi.md`
     - `LinKit.Outbox` -> `outbox.en.md` & `outbox.vi.md`
     - `LinKit.Outbox.Data` -> `outbox-data.en.md` & `outbox-data.vi.md`
     - `LinKit.Resilience` -> `resilience.en.md` & `resilience.vi.md`
     - `LinKit.Sagas` -> `sagas.en.md` & `sagas.vi.md`
     - `LinKit.Tenancy` -> `tenancy.en.md` & `tenancy.vi.md`
     - `LinKit.Json` -> `json.en.md` & `json.vi.md`
     - General architecture or overview changes -> `overview.en.md`, `overview.vi.md`, `architecture.en.md`, `architecture.vi.md`, and `library.json` (plus `E:\Workspace\My\quoclinh-web\src\Host\QuocLinh.Web.Host\wwwroot\content\docs\libraries.json` when bumping versions).
3. Official documentation & release notes URL: `https://quoclinh.id.vn/docs/linkit` — ensure `<PackageProjectUrl>` and `<PackageReleaseNotes>` in `.csproj` files reference this URL when bumping package versions.

