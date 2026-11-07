# Đánh giá hệ sinh thái LinKit — source, NuGet và khả năng vận hành

> Chuyển đổi (phiên âm) từ 24 ảnh chụp màn hình `IMG_3049`–`IMG_3072`. Một số đoạn nhỏ chữ mờ được diễn đạt lại; nội dung kỹ thuật giữ nguyên ý.
> Kiểm tra ban đầu: 29/10/2026. Cập nhật mở rộng và kiểm tra lại GitHub HEAD: 30/10/2026.

## 0. Kết luận cập nhật

Source đã được cập nhật (HEAD `ae3898f`, commit 2026-10-30 02:52 UTC: bổ sung hybrid Redis cache, đưa LinKit.Json vào repo, cập nhật Data, RabbitMQ và generator HTTP).

- Đã đối chiếu 11 package stable mới nhất với Core 3.0.0. Đã build một chương trình dùng cả 11 package và một chương trình tham chiếu 11 project từ HEAD, Release/net10.0: 0 warning, 0 error.
- Đã chạy probe package và source: cache, tenancy, resilience, saga, inbox/outbox, SQLite, JSON.
- Đã chạy lại regression Core/gRPC trên package và generator build từ HEAD: 17/18 nhóm cũ được kiểm chứng vẫn còn; GRPC-08 giữ chứng cứ lịch sử.
- Source repository hiện không thiếu Json; tuy nhiên commit trong metadata một số package vẫn trỏ về snapshot cũ.
- Vấn đề mới đáng lưu ý: `DataSession` và `IUnitOfWork` không chia sẻ transaction; Outbox thiếu fencing của lease; relay không tự gắn MessageId; hybrid cache xử lý cache hit có giá trị mặc định và các giới hạn nhất quán L1/L2.

**Đánh giá tổng thể:** ý tưởng hệ sinh thái nhất quán, module nhỏ giúp tránh phụ thuộc framework lớn. Chưa đủ bằng chứng để gọi toàn bộ hệ sinh thái "production-ready" hoặc "tối ưu nhất" hoặc "100% AOT". Giữ định hướng source generation và switch CQRS; ưu tiên sửa contract, transaction, delivery semantics và regression trước khi tối ưu microbenchmark.

**Cấu trúc báo cáo:** mục 1–10 giữ lịch sử Core 3.0.0/Grpc 2.0.0; mục 11–17 là đánh giá mới toàn hệ sinh thái và source HEAD.

## 1. Kết luận điều hành

Mục tiêu của LinKit hợp lý: chuyển discovery, đăng ký DI và adapter CQRS/HTTP/gRPC sang compile-time để giảm boilerplate, giúp code quan sát và thuận lợi hơn cho trimming/AOT.

Tuy nhiên, các phiên bản được kiểm tra còn lỗi đúng đắn cần sửa trước khi tối ưu tốc độ dispatch: mất dữ liệu protobuf, lỗi generic dispatch, bỏ sót handler, code sinh không biên dịch trong một số trường hợp hợp lệ, và lỗi vòng đời background job.

- 18 nhóm lỗi/bất nhất đã kiểm chứng: 10 nhóm P1, 8 nhóm P2.
- Có cả lỗi Core độc lập và lỗi tại ranh giới Core ↔ gRPC.
- Không có bằng chứng benchmark để kết luận switch, dictionary hay typed executor là phương án nhanh nhất.
- Khuyến nghị giữ source generation và chưa thay switch đại trà. Ưu tiên tính đúng đắn, contract thống nhất, diagnostics và regression tests.
- Báo cáo này đề xuất logic sửa; chưa sửa mã nguồn các thư viện.

### 1.1. Phiên bản và độ tin cậy của bằng chứng

| Thành phần | Bản kiểm tra | Cách kiểm chứng |
|---|---|---|
| LinKit.Core | 3.0.0 trên NuGet | Chạy DLL và generator từ package |
| LinKit.Grpc | 2.0.0 trên NuGet | Chạy runtime và generator từ package; đây là bản cao nhất trong index lúc kiểm tra |
| Protobuf/gRPC tooling | Grpc.Tools 2.71.0 | Biên dịch `.proto` thật, sinh client và service base |
| Môi trường | Windows, .NET SDK 10.0.402, Release/net10.0 | Build và chạy các chương trình tái hiện |
| Source tham khảo | Commit `0155afd2417971b04c292b290b415c742763512c` | Chứa được cả hai package khai báo trong `.nuspec` |

Lưu ý truy xuất nguồn: source ở commit package khai báo không hoàn toàn trùng code được generator phát hành sinh ra. Ví dụ package sinh namespace theo assembly còn source snapshot có các namespace cố định. Vì vậy:

1. Kết luận về bản phát hành ưu tiên kết quả chạy package và generated code thực tế.
2. Link source trong từng mục dùng để định vị logic tương ứng, không được hiểu là đã chứng minh binary được build hoàn toàn từ checkout sạch của commit đó.
3. Lỗi phụ thuộc namespace mới, như GRPC-08, lấy bằng chứng từ generated code của package.

### 1.2. Phạm vi và giới hạn

Đã đi sâu vào:

- CQRS dispatch, handler discovery, behaviors và DI được sinh.
- Core mapper: constructor bắt buộc; HTTP adapter: chuyển `Result<T>` sang HTTP result.
- Background jobs: chạy, dừng, lịch, thay đổi cấu hình và ghi lịch sử.
- gRPC server/client adapters: mapping, constructor, exception translation.
- gRPC runtime: ownership/disposal và ambient context.
- Quét pattern hiệu năng trên 55 file C# runtime của Core và Grpc trong source snapshot; kiểm tra thêm generated runtime code của chương trình thử.

Chưa thực hiện:

- Kiểm toàn bộ mọi tổ hợp DI, mapper, endpoint hay messaging.
- Test broker thật, HTTP/2 qua mạng thật, TLS/authentication, deadline qua mạng, streaming.
- Publish Native AOT, kiểm tra mọi TFM/RID, benchmark throughput/allocation.
- Review các package riêng như LinKit.Data, Outbox, Kafka, RabbitMQ (ở bản kiểm tra đầu).
- Kiểm toán bảo mật chuyên biệt.

Client probe dùng protobuf client thật với `CallInvoker` giả lập trả response xác định; server probe gọi trực tiếp adapter với `ServerCallContext` giả lập. Vì vậy lỗi xảy ra ở dispatch/mapping trước hoặc sau transport; cách này đủ xác nhận các lỗi được nêu nhưng không thay thế integration test qua mạng.

### 1.3. Thang ưu tiên

- **P1** — sửa trước khi coi đường chức năng liên quan là ổn định: mất dữ liệu, crash, code sinh không biên dịch, bỏ sót handler.
- **P2** — sửa sớm: semantics không nhất quán, báo lỗi sai, observability/configuration/lifetime có vấn đề.
- Đề xuất thiết kế/hiệu năng: tách riêng; không gán tốc độ cải thiện khi chưa đo.

Đây là mức ưu tiên kỹ thuật, không phải phân loại lỗ hổng bảo mật.

## 2. Checklist quét hiệu năng

Đếm theo số dòng khớp pattern trong 55 file runtime, không cộng code của generator vào runtime. Các con số dưới đây là tín hiệu cần đọc ngữ cảnh, không phải lỗi số allocation.

| Recipe | Số dòng khớp | Đánh giá sau đọc ngữ cảnh |
|---|---|---|
| `IndexOf("literal")` không có comparison | 0 | Không thấy trong phạm vi quét |
| `Substring` | 0 | Không thấy |
| `StartsWith`/`EndsWith` literal không comparison | 0 | Không thấy |
| `Contains("literal")` | 0 | Không thấy |
| `ToLower()`/`ToUpper()` | 0 | Không thấy |
| `Replace`; chuỗi 3 `Replace` trên một dòng | 0/0 | Không thấy |
| `params` | 3 | Đều là attribute constructors, không phải per-request hot path |
| LINQ `Any`/`All` trên `char` | 0 | Không thấy |
| `async void` | 0 | Không thấy |
| `.Result`/`.Wait()` | 1 | `JobRunner.Stop`, xem CORE-07 |
| `Task.Run` | 3 | Đều ở JobRunner; 2 chỗ launch execution không giữ task |
| `ValueTask`; `stackalloc`; `new byte[...]` | 0/0/0 | Không có double-await ValueTask hoặc stackalloc-loop trong phạm vi |
| `string.Format` | 0 | Không thấy boxing qua API này |
| `ContainsKey` | 1 | Không phải mẫu ContainsKey rồi indexer cùng dictionary |
| Static `Dictionary` / static `FrozenDictionary` | 0/0 | Không có cập nhật viên này; không áp dụng FrozenDictionary cho cache mutable |
| `new List` / `new Dictionary` viết tường minh | 1/2 | Không bao gồm `new()` hoặc collection expression; không suy thành tổng allocation |
| `StringComparer.CurrentCulture` | 0 | Không thấy |
| LINQ `Select/Where/Cast/Take/Aggregate` | 1 | Lọc config; không coi là hot-path tự động |
| LINQ `Any`/`All` | 1 | Kiểm tra DI registration |
| `RegexOptions.Compiled` / `new Regex` / `GeneratedRegex` | 0/0/0 | Không có regex runtime trong phạm vi |
| `new HttpClient` / `new FileStream` | 0/0 | Không có các pattern này |
| `new JsonSerializerOptions` | 2 | Một tại lúc cấu hình message JSON; một tạo mỗi lần ghi history |
| `JsonSerializer.Serialize/Deserialize...` | 5 | 4 có metadata/context trực tiếp, bỏ qua options; 1 overload header dùng reflection |
| Class sealed / class không sealed, loại dòng comment | 35/20 | 35/55 declarations của hai nhóm; không bao gồm static/abstract class |
| `IEquatable<T>` | 1 | JobConfig, nhưng thiếu `IsLogHistory` trong equality |
| `Activator.CreateInstance` | 1 | Fallback gRPC client activation, không phải generated fast-path |

Kiểm tra bổ sung trên mediator mẫu có behaviors:

- 5 vị trí gán `next` bằng lambda; đây không phải số object allocation.
- 1 vị trí `List<Task>` cho notification song song.
- Không có LINQ runtime, `.Wait()`, `.Result` hoặc `async void` trong mediator mẫu.
- Nhánh không behavior trả thẳng Task của handler.

Đối chiếu xuyên file:

- Notification await dùng `ConfigureAwait(false)`; pipeline void wrapper không dùng. Cần policy thư viện nhất quán nhưng không coi đây là bằng chứng tăng throughput.
- Message JSON giữ options dùng lại, trong khi file history tạo options và encoder mỗi lần.
- Có API header nhận `JsonSerializerContext` và API không nhận context; không được suy ra rằng mọi API Grpc đều AOT-safe.
- LINQ/Replace bên trong source generator chủ yếu là chi phí build, không phải request latency.

## 3. Danh sách lỗi/bất nhất đã kiểm chứng

| ID | Ưu tiên | Thành phần | Vấn đề | Bằng chứng |
|---|---|---|---|---|
| CORE-01 | P1 | CQRS + gRPC client | Covariant response làm cast Task lỗi | Runtime cả hai mediator |
| CORE-02 | P1 | CQRS | `ICommand` và `ICommand<Unit>` dispatch khác nhau | Runtime Core |
| CORE-03 | P1 | Discovery | Một handler nhiều interface chỉ được xử lý một interface | Runtime notification |
| CORE-04 | P2 | Behaviors/docs | Thứ tự nhóm và cú pháp Order không khớp tài liệu | Runtime + metadata DLL |
| CORE-05 | P1 | Mapper | Thiếu constructor argument bị thay bằng default/null | Runtime mapping |
| CORE-06 | P2 | HTTP adapter | Result thành công bỏ qua 202/204 | Generated helper runtime |
| CORE-07 | P1 | Background jobs | Stop không đợi execution, completion bị lỗi | Runtime, NullReferenceException |
| CORE-08 | P2 | Scheduler | Lịch sai biến thành delay 5 phút không báo lỗi | Runtime + source |
| CORE-09 | P2 | Job configuration | Đổi `IsLogHistory` không làm config khác nhau | Runtime equality + reload source |
| CORE-10 | P2 | Job history | Ghi thất bại nhưng Task báo thành công | Runtime filesystem failure |
| GRPC-01 | P1 | Mapping | Mất repeated/map cả khi response cùng kiểu protobuf | Runtime client và server |
| GRPC-02 | P1 | Mapping | Transform Guid/string sai hoặc không được áp dụng | Hai negative compilation probes |
| GRPC-03 | P1 | Client generator | Request không có property mapping sinh `new T;` | Negative compilation probe |
| GRPC-04 | P1 | Client generator | Constructor chỉ map một phần vẫn được chọn | Negative compilation probe |
| GRPC-05 | P2 | Exception mapping | `ValidationException` của Core thành Internal | Runtime server |
| GRPC-06 | P2 | DI ownership | Factory dispose lại dependency do DI sở hữu | Runtime đếm Dispose |
| GRPC-07 | P2 | Context | Scope lồng nhau không khôi phục context ngoài | Runtime |
| GRPC-08 | P1 | Server generator | Thiếu qualification cho extension type ở class library/console | Negative compilation probe |

## 4. Lỗi Core và logic sửa

### CORE-01 — Covariance hợp lệ ở request nhưng không hợp lệ ở Task

**Tái hiện:** query triển khai `IQuery<string>` được gán sang `IQuery<object>`. Lỗi gọi qua Core mediator và gRPC mediator đều gây `InvalidCastException`.

```csharp
IQuery<object> query = new TextQuery(); // TextQuery : IQuery<string>
await mediator.QueryAsync(query);
```

**Nguyên nhân:** generated dispatch dùng `(Task<TResponse>)(object)taskOfConcreteResponse`. `Task<string>` không thể cast thành `Task<object>`, dù `IQuery<out T>` cho phép covariance.

**Nguồn:** Core dispatch, gRPC dispatch.

**Logic sửa đề xuất:**

1. Giữ fast-path khi response generic của caller trùng response của handler.
2. Với widening hợp lệ, await Task gốc rồi chuyển giá trị kết quả, không cast Task.
3. Kiểm tra compatibility trước khi chạy handler, tránh chạy command có side effect rồi mới phát hiện lỗi kiểu.
4. Chia sẻ cùng contract dispatch cho Core và gRPC để không sửa lệch nhau.
5. Nếu muốn bỏ covariance, phải ở major version và ghi migration; không âm thầm thu hẹp API hiện tại.

**Test bắt buộc:** exact response, string → object, derived → base/interface, exception/cancellation không đổi, response value type không bị cho phép widening ngoài contract, command không chạy hai lần.

### CORE-02 — ICommand bị loại khỏi generic dispatch

**Tái hiện:** `SendAsync(new VoidCommand())` thành công nhưng gọi cùng instance qua `ICommand<Unit>` ném `HandlerNotFoundException`.

**Nguyên nhân:** `ICommand : ICommand<Unit>` nhưng generator chia switch theo `ResponseType != Unit`, loại request này khỏi overload generic.

**Nguồn:** lọc Unit.

**Logic sửa:**

- Chuẩn hóa descriptor command về `(RequestType, ResponseType)`; void dùng `Unit`.
- Nội bộ luôn có executor `Task<Unit>`. Overload trả `Task` chỉ chuyển tiếp, không cần dựng một pipeline khác.
- Cho cả command triển khai `ICommand` và command chỉ triển khai `ICommand<Unit>` vào dispatch phù hợp.
- Áp dụng cùng normalization cho HTTP/gRPC/job adapters, không phụ thuộc interface nào xuất hiện đầu tiên.

**Test:** concrete command, biến `ICommand`, biến `ICommand<Unit>`, `SendAsync<Unit>`, command có behaviors và command chỉ triển khai generic Unit.

### CORE-03 — Discovery chỉ lấy handler interface đầu tiên

**Tái hiện:** class triển khai `INotificationHandler<First>` và `INotificationHandler<Second>`. Publish hai notification chỉ tăng counter một lần; generated code chỉ có First.

**Nguyên nhân:** discovery dùng `AllInterfaces.FirstOrDefault`. Handler thứ hai không được đưa vào descriptor/registration. Default notification branch trả `Task.CompletedTask`, nên thiếu registration không phát lỗi.

**Nguồn:** `GetHandlerInfo`.

**Logic sửa:**

- Thu thập tất cả closed handler interfaces và sinh một descriptor cho mỗi cặp implementation/interface.
- Dedupe theo symbol identity; không dedupe cả implementation rồi mất các contract khác.
- Commands/queries kiểm tra một handler trên mỗi contract; notification cho nhiều handler.
- Giữ policy no-subscriber nếu đó là thiết kế mong muốn, nhưng không dùng policy này để che discovery bị thiếu.

**Test:** class nhiều query interfaces, nhiều notifications, hỗn hợp query/notification, duplicate commands, multi-assembly discovery.

### CORE-04 — Contract thứ tự behavior và ví dụ Order chưa nhất quán

**Tái hiện:**

- Global order 20, query-contract order 10 → thực thi `query-10 → global-20 → handler`.
- README mô tả ưu tiên nhóm global trước contract.
- `CqrsBehaviorAttribute.Order` trong DLL không có setter; ví dụ `Order = 1` không phù hợp API. Constructor argument positional `1` hoạt động.

**Nguồn:** behavior ordering, attribute.

**Logic sửa:**

- Quyết định rõ: global/contract cùng một order space, hoặc ưu tiên nhóm trước rồi mới Order.
- Nếu chọn ưu tiên nhóm, descriptor cần `GroupRank`; sort theo `(GroupRank, Order, StableId)`.
- Specific behaviors giữ thứ tự khai báo và đặt gần handler như contract.
- Trường hợp trùng Order cần tie-breaker xác định, không dựa vào thứ tự HashSet/symbol traversal.
- Cập nhật README dùng `[CqrsBehavior(typeof(IRequest<>), 1)]`, hoặc bổ sung named property có setter và cập nhật generator đọc NamedArguments.

**Test:** before/after trace của pipeline, short-circuit, exception unwind, thứ tự bằng nhau, local và external behaviors, biên dịch ví dụ README trong CI.

### CORE-05 — Mapper tự điền null cho constructor argument bắt buộc

**Tái hiện:** map source chỉ có `Name` sang `record RequiredDto(string Name, string Required)` vẫn build không warning và chạy; `Required` nhận null.

**Nguyên nhân:** khi không tìm được constructor map đầy đủ, generator chọn constructor và sinh `default(T)!` cho tham số thiếu. Null-forgiving che vấn đề thay vì giải quyết.

**Nguồn:** constructor fallback.

**Logic sửa:**

1. Chỉ chọn constructor accessible và có mọi tham số bắt buộc được map/chuyển kiểu hợp lệ.
2. Optional parameter được dùng default khai báo thật.
3. Nếu không có constructor hợp lệ: phát diagnostic tại mapping configuration, liệt kê tham số còn thiếu.
4. Muốn dùng null/default phải cấu hình rõ, không tự động áp dụng.
5. Kiểm tra required/init members và nullability cùng quá trình validation.

**Test:** record bắt buộc thiếu field phải fail với diagnostic LinKit; optional argument hoạt động; explicit map hoạt động; constructor inaccessible không được chọn.

### CORE-06 — HTTP Result thành công bỏ qua status cấu hình

**Tái hiện:** gọi helper `ToResult` được generator sinh với `Result<string>.Success("value")`; status mong muốn 202 và 204 đều trả `IStatusCodeHttpResult.StatusCode = 200`. Phép thử gọi helper bằng reflection để quan sát đúng nhánh adapter, chưa chạy HTTP server.

**Nguyên nhân:** nhánh Result có value chỉ đặc hóa 201; các status khác chuyển sang `Ok(value)`.

**Nguồn:** `ToResult`.

**Logic sửa:**

- Sau khi unwrap Result thành công, dùng một policy success chung với response thường.
- 204 phải không có body.
- 202 phải thực sự trả 202; 201 phải giữ semantics Created, hỗ trợ Location nếu API có dữ liệu đó.
- IResult trả từ handler giữ nguyên quyền điều khiển.
- Sinh OpenAPI metadata từ cùng descriptor/policy, tránh tài liệu 202 nhưng runtime 200.

**Test:** Result và `Result<T>`, value null/non-null, status 200/201/202/204, custom IResult; integration test xác nhận cả status và body thực.

### CORE-07 — Stop background job không đợi execution và dispose quá sớm

**Tái hiện xác định:**

1. Start runner; gọi `TriggerNowAsync`.
2. Executor báo đã vào và chờ TaskCompletionSource; chưa kết thúc ngay khi cancel.
3. Gọi `Stop()`.
4. Trigger vẫn chưa xong; cho executor kết thúc.
5. Task trigger ném `NullReferenceException`.

**Nguyên nhân:** Stop chỉ đợi `_runnerTask` — vòng scheduler, không phải tất cả execution. Sau đó dispose và gán null `_parallelLimiter`. `TriggerNowAsync` còn chạy và gọi `_parallelLimiter.Release()` trong finally. Scheduled executions cũng được launch mà không giữ task để drain.

**Nguồn:** JobRunner, manager `StopAsync`.

**Logic sửa:**

1. Thiết kế state `Created → Running → Stopping → Stopped`; chuyển trạng thái nguyên tử.
2. Đăng ký tất cả manual/scheduled execution vào tập task đang chạy trước khi launch.
3. `StopAsync` đóng admission, cancel scheduler/executions, await scheduler và drain task set.
4. Chỉ dispose CTS/semaphore khi không còn task sử dụng.
5. Nếu shutdown timeout, báo rõ timeout; không giả định cancel đã dừng tác vụ và không dispose dependency còn được dùng.
6. Manager await ngoài lock; tránh `.Wait()` trong shutdown async và tránh làm tổng thời gian chờ tăng tuần tự theo số job.
7. `StopAsync` idempotent; subscription OnChange cũng cần được dispose khi manager kết thúc.

**Test:** trigger đang chạy khi Stop; executor tôn trọng/không kết thúc ngay sau cancellation; nhiều execution; Stop hai lần; config reload đồng thời trigger; không unobserved exceptions.

### CORE-08 — Lịch sai bị biến thành lịch chạy 5 phút

**Tái hiện:** Daily với `TimeOfDay = "not-time"` trả `00:05:00`, không exception hoặc diagnostic.

**Nguyên nhân:** Scheduler trả `DefaultErrorDelay` cho input sai và có catch bao ngoài. Invalid timezone còn có fallback UTC. Runner dùng delay đó để chạy job, không chỉ để retry việc đọc cấu hình.

**Nguồn:** Scheduler.

**Logic sửa:**

- Validate cấu hình trước khi kích hoạt: interval > 0, MaxParallel > 0, giờ trong ngày hợp lệ, timezone tồn tại, ngày tuần/tháng theo policy.
- Phân biệt `InvalidSchedule` với `NextOccurrence`. Invalid không được chuyển thành lịch nghiệp vụ.
- Reload lỗi: giữ last-known-good hoặc disable job theo policy rõ ràng và log lỗi; không lặng lẽ thay timezone.
- Inject TimeProvider, chốt policy DST invalid/ambiguous time và test deterministic.

**Test:** giờ sai, timezone sai, interval 0/âm, MaxParallel 0, DST gap/overlap, ngày cuối tháng. Không coi việc trả số phút hữu hạn là tiêu chí thành công.

### CORE-09 — IsLogHistory không tham gia so sánh config

**Tái hiện:** `new JobConfig { IsLogHistory = false }.Equals(new JobConfig { IsLogHistory = true })` trả true.

**Nguyên nhân:** `Equals`/`GetHashCode` không xét `IsLogHistory`, trong khi reload dựa vào Equals để giữ hoặc tạo runner mới. Với config instance mới chỉ đổi cờ này, runner tiếp tục giữ cấu hình cũ.

**Nguồn:** JobConfig equality.

**Logic sửa:** thêm `IsLogHistory` vào equality/hash; tốt hơn dùng immutable validated snapshot chứa đầy đủ fields ảnh hưởng hành vi. Nếu một số fields được update không restart, phân loại chúng tường minh.

**Test:** thay từng field riêng lẻ, đặc biệt false → true → false của history; reload phải thay đổi hành vi thực, không chỉ kiểm tra Equals.

### CORE-10 — History logger nuốt lỗi ghi file

**Tái hiện:** trỏ history path vào một thư mục ngẫu nhiên chắc chắn chưa tồn tại; `LogAsync` hoàn tất thành công, không có file và không có lỗi.

**Nguyên nhân:** catch rỗng trong `FileJobHistoryLogger` làm lớp `BackgroundJobExecution` không nhận được lỗi để log. Tạo `JsonSerializerOptions` và encoder trên mỗi lần ghi còn tăng chi phí không cần thiết.

**Nguồn:** `FileJobHistoryLogger`.

**Logic sửa:**

- Propagate lỗi I/O cho caller đang có logger, hoặc trả outcome tường minh theo policy; không success-shaped fallback.
- Validate path ở startup; chỉ tạo directory nếu đó là contract được tài liệu hóa.
- Nếu history best-effort: vẫn log/metric failure. Nếu bắt buộc audit: phải biểu diễn failure ở execution outcome.
- Cache immutable serializer options/context; dispose semaphore khi không thao tác.

**Test:** thư mục không tồn tại, quyền truy cập bị từ chối, cancellation, concurrent writes; mỗi thất bại phải quan sát được.

## 5. Lỗi gRPC và logic sửa

### GRPC-01 — Mất dữ liệu repeated/map ở client và server

**Tái hiện:** protobuf response có `Id`, `repeated int32 Numbers = [7, 9]`, `map<string,string> Labels` một phần tử. Handler hoặc wire trả đúng message đó; adapter trả `Id` nhưng `Numbers=[]`, `Labels.Count=0`.

**Đặc biệt:** CQRS response chính là cùng kiểu protobuf response. Không cần DTO khác nhau để gặp lỗi.

Generated code quan sát được:

```csharp
return new WireReply { Id = grpcResponse.Id };
```

Server cũng tạo WireReply mới chỉ copy `Id`.

**Nguồn:** server response mapping, client mapping emission.

**Logic sửa:**

1. Nếu source/destination cùng symbol và không có custom mapping: trả thẳng message, hoặc Clone nếu contract yêu cầu ownership riêng. Không dựng message mất fields.
2. Phân loại mapping thành scalar, nested message, repeated scalar, repeated message, map và oneof/presence.
3. Repeated scalar: AddRange/loop copy giá trị; không `new int { }` hoặc tạo item rỗng.
4. Repeated message: map từng item hoặc Clone theo policy. Map: giữ cả key/value.
5. Shape chưa hỗ trợ phải có diagnostic, không bỏ qua field mà vẫn tạo thành công giả.
6. Cùng kiểu protobuf cần bảo toàn unknown fields/presence; trả trực tiếp hoặc Clone phù hợp hơn copy thủ công.

**Test:** cùng message type, DTO khác type, repeated primitives/messages, map, empty collection, optional/oneof, nested null, unknown fields qua round-trip.

### GRPC-02 — Transform được tính sai chiều hoặc bị bỏ khi emit

**Tái hiện qua hai build độc lập:**

- Server: wire `string Id` → CQRS `Guid Id`: CS0029, không chuyển string sang Guid.
- Client: CQRS `Guid Id` → wire `string Id`: CS0029, không chuyển Guid sang string.

**Nguyên nhân:** server tính request map theo thứ tự CQRS → protobuf rồi áp dụng vào chiều protobuf → CQRS. Client lấy map có `TransformTemplate` nhưng emitter chỉ gán property trực tiếp.

**Nguồn:** server request maps, client request emission.

**Logic sửa:**

- Mỗi mapping plan có source/destination rõ: server request wire → CQRS, server response CQRS → wire, client chiều ngược lại.
- Một expression builder dùng chung cho constructor, property, collection item; không để emitter tự bỏ transform.
- Kiểm tra conversion bằng Roslyn, chỉ emit khi conversion hợp lệ hoặc converter explicit.
- Guid sai định dạng: báo InvalidArgument theo policy, không biến thành Internal.
- Timestamp cần policy UTC/null/presence; numeric narrowing cần policy overflow.

**Test:** Guid/string hai chiều và cả request/response; Timestamp; enum; invalid Guid; nullable; type incompatible có diagnostic dễ hiểu.

### GRPC-03 — Không có request mapping thì sinh cú pháp `new` không hợp lệ

**Tái hiện:** request CQRS không có property map tới protobuf request. Generated client chứa `var grpcRequest = new WireRequest;`, build lỗi CS1526.

**Nguồn:** nhánh requestMappings rỗng.

**Logic sửa:** object construction luôn gồm argument list hoặc initializer hợp lệ. Với zero assignments, sinh `new WireRequest()`. Sau đó có diagnostics riêng nếu zero mapping là ngoài ý muốn.

**Test:** empty request thực sự (`Google.Protobuf.WellKnownTypes.Empty`), request có fields nhưng không map được, constructor không parameterless.

### GRPC-04 — Constructor map một phần được ưu tiên hơn fallback hợp lệ

**Tái hiện:** response DTO có constructor rỗng và constructor `(string id, int missing)`. Wire chỉ có `id`. Generator sinh `new PartialDto(grpcResponse.Id)` và build lỗi CS7036, dù có fallback constructor rỗng hợp lệ.

**Nguyên nhân:** danh sách partial constructor arguments vẫn được lưu; emit dùng `ResponseConstructorParameters.Any()` thay vì trạng thái "constructor đã validate hoàn toàn".

**Nguồn:** constructor collection, selection khi emit.

**Logic sửa:**

- Chỉ đưa constructor vào construction plan khi tất cả required parameters map được và type-compatible.
- Nếu bỏ candidate, bỏ cả partial argument list.
- Ưu tiên constructor rỗng + member mapping khi phù hợp; immutable record chọn constructor đầy đủ.
- Không có plan hợp lệ thì diagnostic, tuyệt đối không tự điền default như CORE-05.

**Test:** parameterless + partial constructor, record, overloads, optional params, private ctor, transformation trong constructor, required/init members.

### GRPC-05 — Exception validation của Core bị dịch thành Internal

**Tái hiện:** handler ném `LinKit.Core.Validation.ValidationException`; server adapter trả `RpcException(StatusCode.Internal)`.

**Nguyên nhân:** generated code import `System.ComponentModel.DataAnnotations` và catch `ValidationException` của namespace đó, không phải exception của Core.

**Nguồn:** catch validation.

**Logic sửa:**

- Catch đầy đủ tên của hai exception nếu thư viện hỗ trợ cả hai.
- Core validation → InvalidArgument; giữ field errors bằng contract metadata/details có cấu trúc.
- Có một error translator dùng chung; tránh mỗi adapter tự suy diễn exception name.
- `Result<T>` failure cũng cần policy chuyển thành gRPC status hoặc envelope explicit; không tự map success payload.
- Không để general `InvalidOperationException` mặc nhiên che mọi lỗi programmer thành FailedPrecondition nếu chưa có policy.

**Test:** Core validation, DataAnnotations validation, existing RpcException, cancellation/deadline, domain not-found/conflict, unexpected exception được log và có thông điệp an toàn.

### GRPC-06 — Factory dispose dependency do DI sở hữu

**Tái hiện:** đăng ký singleton channel/interceptor providers bằng DI, resolve `DefaultGrpcClientFactory`, dispose ServiceProvider. Mỗi provider nhận `Dispose()` hai lần.

**Nguyên nhân:** factory dispose các dependency injected vào constructor; DI container cũng dispose chính các singleton đó.

**Nguồn:** `DefaultGrpcClientFactory.Dispose`.

**Logic sửa:**

- Ownership rule: component chỉ dispose resource nó tạo/sở hữu.
- Factory không dispose providers được inject; container chịu trách nhiệm.
- Channel provider sở hữu channel và dispose từng channel đúng một lần.
- Nếu muốn API tự tạo provider ngoài DI, dùng ownership option/constructor riêng có contract rõ.

**Test:** provider custom không idempotent; DI disposal counts = 1; factory manual mode; shutdown không dispose channel khi còn in-flight call ngoài policy.

### GRPC-07 — Ambient context scope không phục hồi scope cha

**Tái hiện:** mở scope A, mở rồi dispose scope B; Current không còn A mà bằng null.

**Nguyên nhân:** BeginScope chỉ set context mới; `Scope.Dispose` luôn clear, không giữ previous.

**Nguồn:** `GrpcContextAccessor`.

**Logic sửa:**

- BeginScope lưu previous context và trả scope handle giữ previous đó.
- Dispose theo LIFO khôi phục previous; nên handle idempotent để tránh copy struct/double dispose làm hỏng context.
- Nếu cần chống child task giữ context đã kết thúc, cân nhắc holder/clearing semantics; không chỉ giải quyết bằng static global.
- Hoặc truyền context explicit ở các API nội bộ không cần ambient state.

**Test:** nested scopes, exception trong scope, qua await, hai requests song song, child execution context; không chỉ kiểm tra Current ở một call đơn.

### GRPC-08 — Generated server code phụ thuộc global using của Web SDK

**Tái hiện:** project `Microsoft.NET.Sdk`, có FrameworkReference Microsoft.AspNetCore.App và đầy đủ packages, build adapter server lỗi CS0103: không tìm thấy `GrpcEndpointRouteBuilderExtensions`.

Thêm `global using Microsoft.AspNetCore.Builder;` vào probe thì build thành công. Đây là workaround để tiếp tục thử nghiệm, không phải sửa package.

**Nguyên nhân:** package sinh extension trong namespace assembly-specific nhưng gọi `GrpcEndpointRouteBuilderExtensions` không fully qualified và thiếu using đúng. Web SDK có thể tình cờ che lỗi nhờ implicit usings.

Generated code lỗi:

```csharp
GrpcEndpointRouteBuilderExtensions.MapGrpcService<LinKitReviewService>(endpoints);
```

Logic sửa: sinh tên đầy đủ:

```csharp
global::Microsoft.AspNetCore.Builder.GrpcEndpointRouteBuilderExtensions
    .MapGrpcService<LinKitReviewService>(endpoints);
```

Toàn bộ framework symbols trong generated code nên được resolve và render fully qualified; không dựa vào using của consumer.

**Test:** class library/console/Web SDK, ImplicitUsings bật/tắt, namespace consumer khác nhau, consumer có type trùng simple name.

## 6. Thiết kế mục tiêu và giải pháp logic xuyên suốt

### 6.1. Một mô hình contract chung cho CQRS và adapters

Hiện nhiều generator tự nhận diện command/query, response, Unit và mapping bằng tên/chuỗi riêng. Đó là nguồn phát sinh khác biệt giữa Core, HTTP và gRPC.

Đề xuất descriptor dùng chung ở tầng phân tích:

```text
RequestDescriptor
  RequestSymbol
  Kind: Command | Query | Notification
  ResponseSymbol            // Unit cho command không kết quả
  HandlerBindings[]
  BehaviorPlan[]

MappingPlan
  SourceSymbol
  DestinationSymbol
  ConstructionPlan          // chỉ tồn tại khi hợp lệ
  MemberPlans[]             // scalar, object, repeated, map, presence
  Diagnostics[]
```

- Phân tích bằng symbol identity, `OriginalDefinition` và conversion rules.
- Bỏ generic arguments rồi coi hai closed contracts là tương đương.
- Constraint resolution không nên phụ thuộc type parameter có tên đúng `TRequest`.
- Chỉ chuyển symbol thành chuỗi ở bước emit.
- Shape chưa hỗ trợ → diagnostic gắn với source của người dùng, không sinh C# hỏng hoặc bỏ dữ liệu.
- Chia sẻ code phân tích ở build-time; không bắt buộc thêm abstraction vào hot runtime path.

### 6.2. CQRS switch/case: giữ, đo, rồi mới thay

Type-pattern switch không tự động là integer jump table O(1). Compiler/JIT lowering phụ thuộc pattern và runtime. Cũng không nên khẳng định mọi request luôn đi qua đủ N kiểm tra.

Giá trị hiện tại:

- Generated references cụ thể, dễ debug.
- Không runtime discovery trên đường dispatch này.
- Không dictionary lookup cho dispatch.
- Fast-path không behavior trả thẳng Task.

Giới hạn cần đo: method/code size khi hàng trăm/hàng nghìn request; JIT/cold start và Native AOT binary size; phân bố request và ảnh hưởng case ordering/type hierarchy; API chỉ generic theo response nên vẫn cần chọn concrete request lúc runtime.

Hướng khả thi nếu số liệu chứng minh cần tối ưu:

1. Giữ `IMediator`/`IGrpcMediator` làm compatibility surface.
2. Generated HTTP/gRPC endpoints đã biết request type có thể gọi typed executor trực tiếp.
3. Typed executor phải chạy đúng toàn bộ behavior/validation/transaction/tracing; không bypass mediator semantics.
4. Large dynamic dispatch có thể thử dictionary/frozen dictionary, nhưng exact-type lookup phải bảo toàn hoặc chủ động thay đổi semantics kế thừa của type-pattern switch.
5. Không cache handler scoped/transient vào static field để giảm chi phí.

### 6.3. Pipeline: đơn giản hóa trước khi đổi collection

🟡 **Moderate** — chi phí dựng pipeline mỗi lần gọi, cần đo nếu hot path.

- Handler delegate + mỗi behavior wrapper có thể tạo closure/delegate.
- Nhánh void có chuyển `Task → Task<Unit> → Task` và nhiều async wrapper.
- Sinh pipeline không có nghĩa pipeline đã được precompose hoàn toàn.

**Đề xuất:** giữ `Task<Unit>` nội bộ; sinh execution plan xác định. Nếu đổi sang executor object/decorator API `next` có state explicit, phải giữ short-circuit, retry gọi next nhiều lần, cancellation, reentrancy và DI lifetime. Đây có thể là thay đổi API lớn, không nên triển khai bằng cache closure theo request scope.

Không chuyển mọi Task sang ValueTask: handler I/O thường thật sự async, còn ValueTask có quy tắc tiêu thụ phức tạp.

### 6.4. gRPC channel/client ownership và cache

ℹ️ **Info** — điểm yếu thiết kế xác định từ source, chưa stress-test thành lỗi runtime riêng.

- Default provider, factory channel cache, invoker cache, client cache là nhiều lớp lưu trạng thái liên quan.
- Provider có nhánh thay channel Shutdown, nhưng factory/invoker đã cache không tự động nhìn thấy replacement.
- ConcurrentDictionary value factory có thể chạy nhiều lần khi cạnh tranh; channel được tạo nhưng giá trị bị loại có nguy cơ không được dispose.
- Generated mediator tự `new` protobuf clients thay vì dùng `GetClient<T>()`, nên cache/activator path không được dùng bởi mọi đường gọi.

**Đề xuất:**

- Một owner duy nhất cho channel, thường sống cùng host.
- Cache construction bằng Lazy hoặc cơ chế publish/dispose loser rõ ràng nếu thực sự tạo resource khi concurrent.
- Không tự rotate channel khi chưa có invalidate policy cho invoker/client; channel dài hạn đã có reconnect semantics của gRPC.
- Chốt generated mediator dùng factory client hay dùng invoker trực tiếp; tài liệu hóa khác biệt.
- Metadata lấy per-call; providers singleton không giữ mutable request state.
- Deadline là budget end-to-end. Bổ sung call context/options nếu cần per-call deadline; không mặc định retry command khi chưa có idempotency.

### 6.5. AOT/trimming và đóng gói

- Generated activator, concrete handler registrations và source-generated JSON là nền tảng tốt.
- Reflection fallback trong client factory và overload header JSON không có context vẫn tồn tại. Phải phân loại API nào AOT-safe thay vì gắn nhãn cho toàn package.
- NuGet Grpc còn dependency Grpc.Core 2.46.6 trong graph trong khi runtime dùng Grpc.Net.Client. Cần rà lại dependency nào thực sự cần; không nên xóa mà chỉ vì cùng chữ "Core".
- Core `.nuspec` có dependency groups rỗng dù một số API runtime/generated code dùng Microsoft.Extensions/ASP.NET. Cần clean-consumer package tests theo từng feature để chứng minh dependency graph đầy đủ, không dựa vào packages vô tình có từ host.
- Không xác nhận Native AOT chỉ bằng việc source generator build được.
- CI pack từ clean checkout, ghi version/commit đúng, tái tạo package và so generated output. Source mismatch hiện tại làm review/debug phát hành khó tin cậy hơn.

### 6.6. Phạm vi sản phẩm và coupling

- Giá trị cốt lõi nên là compile-time wiring đáng tin cậy, không chỉ "nhanh hơn mediator khác".
- CQRS mediator không tự đảm bảo kiến trúc CQRS, transaction boundary hay consistency.
- Request gắn HTTP/gRPC metadata thuận tiện cho CRUD, nhưng cần lựa chọn tách transport contract khỏi use-case contract.
- Không cần tách mọi module thành package ngay. Nếu tách, bắt đầu từ contracts/abstractions nhẹ và optional adapters có dependency rõ.
- Policy attributes chưa được thực thi phải báo diagnostic hoặc ghi rõ là metadata; không để người dùng tưởng retry/cache/validation đã tự chạy.

## 7. Thứ tự triển khai đề xuất

### Đợt A — Chặn mất dữ liệu và generated code hỏng

1. GRPC-01: same-type protobuf fast-path và collection mapping.
2. CORE-01/02/03: dispatch/Unit/discovery đúng.
3. GRPC-02/03/04/08: mapping direction, construction plan, fully qualified code.
4. CORE-05: bỏ constructor default fallback ngầm.

**Điều kiện hoàn tất:** negative compilation probes đổi thành build hợp lệ hoặc diagnostic LinKit đúng; round-trip không mất fields; chưa cần claim hiệu năng.

### Đợt B — Lifetime, lỗi nghiệp vụ và observability

1. CORE-07: async shutdown/drain.
2. GRPC-06/07: ownership/context.
3. GRPC-05 và CORE-06: thống nhất transport outcome.
4. CORE-08/09/10: validate config, history changes, error visibility.
5. CORE-04: behavior ordering và README build tests.

**Điều kiện hoàn tất:** không task dùng semaphore đã dispose; lỗi validation không biến thành Internal; status HTTP đúng; config/history failures quan sát được.

### Đợt C — Đo và tối ưu

- 🟡 Moderate: benchmark pipeline delegate/void adapters trước.
- ℹ️ Info: cache options/encoder của history logger; hiệu quả phụ thuộc tần suất và I/O.
- ℹ️ Info: gỡ allocation tạo response rồi gán response mới trong server mapper; không gán tốc độ cải thiện khi chưa đo.
- Chỉ thay dispatch strategy nếu measured gain đáng kể với workload mục tiêu và không làm xấu cold start/AOT/code size quá mức.

## 8. Ma trận kiểm thử chấp nhận

| Nhóm | Ca tối thiểu | Điều phải assert |
|---|---|---|
| Generic dispatch | Exact, covariance, Unit, value response | Đúng kiểu/kết quả, handler chạy đúng một lần |
| Discovery | Nhiều interfaces, multi-assembly, duplicate | Thiếu registrations; diagnostics deterministic |
| Behaviors | 0/1/5, global/contract/specific, same Order | Trace trước/sau đúng; short-circuit/retry semantics giữ nguyên |
| Mapping constructor | Mutable DTO, record, required/optional, inaccessible | Không tự null/default; diagnostic rõ khi thiếu |
| Protobuf mapping | Scalars, repeated, map, nested, optional, oneof | Bảo toàn dữ liệu/presence/unknown fields theo contract |
| Transformation | Guid, Timestamp, enum, nullable, overflow | Cả hai chiều đúng; invalid input có lỗi dự đoán được |
| gRPC errors | Hai loại validation, RpcException, domain errors | Status/details đúng; unexpected error có log |
| HTTP results | 200/201/202/204, Result, IResult | Status, body, metadata không mâu thuẫn |
| Jobs | Trigger + stop/reload, cancellation, timeout | Drain, idempotent stop, không use-after-dispose |
| Scheduler | Invalid config, DST, month-end | Không tự chạy theo lịch fallback ngầm |
| DI ownership | Singleton custom IDisposable providers | Dispose đúng một lần |
| Context | Nested, await, parallel requests | Restore và isolation đúng |
| Consumer builds | Web/library/console, implicit usings on/off | Không phụ thuộc ambient using |
| Publish | net8/net9/net10 theo support policy; target AOT RIDs | Build/publish/run, trim warnings được xử lý |
| Documentation | Ví dụ public copy nguyên văn | Biên dịch được, dùng đúng overload |

### 8.1. Benchmark để quyết định switch

| Trục | Giá trị đề xuất |
|---|---|
| Số request | 10, 100, 1.000 |
| Phân bố | Một request nóng, uniform, case đầu/cuối |
| Behaviors | 0, 1, 5 |
| Handler | Cached/synchronous Task; async thực sự |
| DI | Cùng lifetime và dependencies cho mọi đối thủ |
| Baselines | Direct call, current switch, typed executor, dictionary/frozen dictionary |
| Runtime | Release JIT có warmup; cold start riêng; Native AOT riêng |
| Metrics | ns/op, allocated bytes/op, code size, startup, publish size, build/incremental generation time |

Không so direct handler không behaviors với mediator có validation/transaction rồi kết luận overhead không hợp lý. Phải giữ cùng công việc/semantics.

## 9. Nhật ký bằng chứng rút gọn

### 9.1. Core CQRS probe

Build Release: 0 warnings, 0 errors.

```text
Exact query: OK
Pipeline order: query-10,global-20,handler
Covariant query error: InvalidCastException
Void command: OK
Generic Unit command error: HandlerNotFoundException
Behavior Order writable: False
Multi-interface notification calls: 1
```

Chạy thêm cấu hình không behaviors: exact query và void command vẫn thành công; covariance và generic Unit vẫn lỗi. Generated no-behavior path trả thẳng handler Task.

### 9.2. Core + Grpc runtime probe

Build Release sau workaround global using của GRPC-08: 0 warnings, 0 errors.

```text
Mapper missing required constructor value: True
HTTP Result<T> requested 202: actual 200
HTTP Result<T> requested 204: actual 200
Client response: id=from-wire, numbers=[], labels=0
Remote covariant query: InvalidCastException
Server response: id=test, numbers=[], labels=0
Core validation gRPC status: Internal
Provider dispose counts: channel=2, interceptor=2
Nested context restores outer: False
Invalid daily schedule delay: 00:05:00
History-only config equality: True
History write to missing directory returns success: True
Job trigger incomplete after Stop: True
Job completion after Stop: NullReferenceException
```

### 9.3. Negative compilation probes

| Cấu hình | Kết quả từ compiler |
|---|---|
| Server/library không global using ASP.NET Builder | CS0103: `GrpcEndpointRouteBuilderExtensions` không tồn tại trong context |
| SERVER_CONVERSION | CS0029: string → System.Guid |
| CLIENT_CONVERSION | CS0029: System.Guid → string |
| CLIENT_EMPTY | CS1526: new expression thiếu argument list/initializer |
| PARTIAL_CONSTRUCTOR | CS7036: thiếu parameter `missing` |

Các probe cô lập ở thư mục session, không thêm project/package reference vào solution LinKit.Json. Không có server ngoài nào được gọi; không publish package hoặc commit thay đổi.

## 10. Tổng kết

### Đánh giá chức năng

| Ưu tiên | Số nhóm đã kiểm chứng | Vấn đề nổi bật |
|---|---|---|
| P1 | 10 | Mất protobuf data, dispatch lỗi, generated code không build, shutdown race |
| P2 | 8 | Exception/status/config/history/context/ownership semantics |
| Tổng | 18 | Số nhóm root-cause, không phải số dòng lỗi hoặc số ca thử |

### Đánh giá performance riêng

| Severity | Count | Top Issue |
|---|---|---|
| 🔴 Critical | 0 | Chưa đo được regression hiệu năng nghiêm trọng |
| 🟡 Moderate | 1 | Pipeline dựng delegate/async adapter per-call, đáng đo trên hot path |
| ℹ️ Info | 2 | History serializer setup; response construction dư thừa |

**Quyết định đề xuất:** chưa viết lại mediator chỉ vì thấy switch/case. Sửa các lỗi đã kiểm chứng, thống nhất symbol/mapping/dispatch model, đưa regression probes vào CI, sau đó benchmark để chọn tối ưu có lợi thật sự.

> Báo cáo có AI hỗ trợ tạo. Các kết quả runtime/compiler trong phần bằng chứng được quan sát trực tiếp, nhưng phạm vi kiểm tra không bao phủ mọi ứng dụng. Nhận định từ source có thể chịu ảnh hưởng của khác biệt source/package; đề xuất hiệu năng chưa có số đo. Cần review thủ công và chạy regression/benchmark trên workload thật trước khi áp dụng production.

## 11. Phiên bản mới nhất và đối chiếu source

### 11.1. Ma trận phiên bản

Phiên bản lấy từ NuGet flat-container index; dependency/commit đọc từ package đã restore. "Mới nhất" ở đây là stable tại thời điểm kiểm tra 30/10/2026, không gồm prerelease.

| Package | Stable đã thử | Version trong HEAD | Commit package khai báo |
|---|---|---|---|
| LinKit.Core | 3.1.0 | 3.1.0 | `e8e774b` |
| LinKit.Messaging.RabbitMQ | 1.3.0 | 1.3.0 | `e8e774b` |
| LinKit.Data | 2.2.0 | 2.2.0 | `e8e774b` |
| LinKit.Sagas | 1.1.0 | 1.1.0 | `29de196` |
| LinKit.Tenancy | 1.1.0 | 1.1.0 | `29de196` |
| LinKit.Outbox | 1.1.0 | 1.1.0 | `29de196` |
| LinKit.Caching | 1.2.0 | 1.2.0 | `e8e774b` |
| LinKit.Outbox.Data | 1.1.0 | 1.1.0 | `29de196` |
| LinKit.Json | 1.0.0 | 1.0.0 | `e8e774b` |
| LinKit.Resilience | 1.1.0 | 1.1.0 | `29de196` |
| LinKit.Grpc | 2.0.0 | 2.0.0 | `0155afd` |

HEAD hiện tại là `ae3898f`, không phải những commit package khai báo. Đã đọc cả source HEAD và decompile DLL net10.0 của 11 package để tránh kết luận chỉ dựa vào metadata.

Ví dụ cụ thể:

- Snapshot `e8e774b` không có project Json, nhưng HEAD đã có `LinKit/LinKit.Json/src/LinKit.Json`.
- Snapshot `29de196` không có các project Outbox, Outbox.Data, Sagas, Tenancy, Resilience mà package tương ứng khai báo.
- Hybrid Redis đã có trong DLL Caching 1.2.0 và source HEAD, dù source tại commit package khai báo không có triển khai đó.

**Kết luận chính xác:** không thể lấy commit metadata cũ làm đại diện cho code hiện hành. Ngược lại, version trong project trùng NuGet và probe cho kết quả giống nhau không chứng minh toàn bộ source/binary byte-for-byte tương đương.

**Đề xuất release engineering:** CI pack từ checkout sạch; ghi đúng RepositoryCommit/SourceLink; gắn tag bất biến theo release; kiểm thử trực tiếp `.nupkg` vừa pack trước khi publish. Không thay nội dung dưới cùng version. Có manifest release chung để người dùng biết tổ hợp package nào đã được test cùng nhau.

### 11.2. Dependency và tương thích

- Data 2.2.0 yêu cầu Core từ 3.1.0; OutboxData 1.1.0 chỉ khai báo Data từ 1.1.0. Restore được không đồng nghĩa mọi tổ hợp major/minor đều có cùng semantics transaction.
- RabbitMQ 1.3.0 dùng RabbitMQ.Client 6.8.1. Đổi sang async API của 7.x không vô điều kiện: cần kiểm tra async API, buffer lifetime, channel ownership và confirms.
- Grpc 2.0.0 còn dependency Grpc.Core 2.46.6 bên cạnh Grpc.AspNetCore 2.71.0. Nên đánh giá có cần native channel stack hay chỉ cần contracts + managed client; không xóa dependency trước khi kiểm tra public API.
- Core 3.1.0 có dependency groups rỗng trong package; source để một số Microsoft.Extensions package ở `PrivateAssets="all"`. Nên bổ sung consumer smoke test trên SDK console/worker tối thiểu, không chỉ Web SDK. Probe hiện tại có FrameworkReference ASP.NET Core, nên không chứng minh dependency closure của Core độc lập đã đầy đủ.

## 12. Đánh giá mục tiêu và từng thư viện

### 12.1. Kiến trúc có hợp lý không?

Có. Một pipeline ứng dụng hợp lý có thể là:

```text
HTTP / gRPC -> CQRS + validation -> Data transaction
                                     + Outbox row
Outbox relay -> RabbitMQ -> tenant scope -> atomic inbox + business effect
                           -> saga state transition + outgoing outbox
```

Cache và resilience là chính sách bao quanh những thao tác được chọn, không phải middleware mặc định áp lên mọi nghiệp vụ. Json là utility độc lập; không cần ép mọi module phải phụ thuộc Json.

Điểm khó không nằm ở có bao nhiêu package mà ở các invariant: một transaction thực; identity message ổn định; retry không nhân đôi side effect; tenant scope không mơ hồ; cache expiry không bị kéo dài vô ý. Đây là nơi cần đầu tư tiếp.

| Thư viện | Điểm tốt/định vị | Đánh giá và ưu tiên |
|---|---|---|
| Core 3.1.0 | Compile-time discovery/DI/CQRS giúp giảm reflection và boilerplate; generated code dễ xem | Giữ hướng thiết kế. Sửa dispatch, handler discovery, mapper và job lifetime trước; 10 nhóm Core cũ vẫn tái hiện |
| Grpc 2.0.0 | Tái dùng application contract qua adapter, giảm glue code | Chưa ổn định ở mapping protobuf/generic/constructor. Ưu tiên bảo toàn repeated/map/unknown fields, diagnostics và ownership |
| Data 2.2.0 | Typed SQL AST, generated metadata/reader, streaming, tenant/version guards, chống concurrent operations trên cùng DataSession | Có nền tảng tốt. Chưa đủ kết luận nhanh hơn EF/Dapper. Việc DataSession và IUnitOfWork là hai transaction owner là vấn đề tích hợp cần xử lý ngay |
| Outbox 1.1.0 | Claim lease, retry scheduling, options validation, TimeProvider | Đúng hướng at-least-once. Thiếu claim fencing, tự gắn message identity và lifecycle purge/replay; batch tuần tự cần giới hạn lease |
| Outbox.Data 1.1.0 | ADO.NET durable store, parameterized values, enlist vào CurrentTransaction, composite inbox key | SQLite rollback với IUnitOfWork hoạt động. Chưa nối với transaction của DataSession; fencing vẫn thiếu; chưa xác nhận 4 DB còn lại |
| RabbitMQ 1.3.0 | Manual ACK/NACK, prefetch, channel reuse, publisher confirms mặc định, header/basic-property bridge, topology options | Confirm chưa đồng nghĩa routed delivery; publish hiện không dùng cancellation; cần poison-message và drain policy |
| Caching 1.2.0 | In-memory single-flight, waiter cancellation tách factory; đã có hybrid Redis | Hybrid chưa giữ cùng semantics cache hit với in-memory. Redis tự viết làm tăng đáng kể trách nhiệm protocol, timeout, pool và consistency |
| Sagas 1.1.0 | API nhỏ cho một step và compensation; compensation dùng token độc lập có timeout | Là step coordinator, không phải durable workflow engine. README đã nói rõ; không coi thiếu exactly-once là "lỗi cam kết" |
| Tenancy 1.1.0 | Context/propagation đơn giản; scope có header khôi phục context ngoài | Cần policy rõ khi thiếu header và abstraction cho custom accessor. Không tự tạo isolation cho SQL/cache/broker |
| Resilience 1.1.0 | Explicit pipeline, retry cap/jitter, TimeProvider, caller cancellation | Cần thống nhất cancellation mặc định và contract timeout. Không nên retry mọi lỗi nghiệp vụ chỉ vì API hỗ trợ retry |
| Json 1.0.0 | Path immutable, lazy wildcard traversal, integer conversion chính xác, DTO qua JsonTypeInfo | Phạm vi nhỏ khớp quả đúng; source đã có ở HEAD. Chưa tìm thấy blocker trong đường thử, nhưng chưa chạy toàn suite/fuzz/AOT/benchmark |

Không nên mở rộng thêm nhiều feature trước khi có integration suite xuyên module. Có thể dùng từng phần có kiểm soát; chưa nên quảng bá cả bộ như một framework distributed application đã hoàn chỉnh.

### 12.2. Những tiến bộ cần ghi nhận công bằng

- Cache README nói capacity/LRU là best-effort, không phải strict memory quota; không gán lỗi vì concurrent write có thể vượt capacity.
- Sagas README nói rõ không có distributed locking, durable event log hoặc reverse orchestration tự động.
- Outbox.Data README đã thừa nhận at-least-once và check-then-mark inbox không phải atomic inbox reservation.
- Resilience README đã mô tả cooperative cancellation. Không thể kết luận timeout cưỡng bức đúng được arbitrary code.
- DataSession kiểm tra concurrent use, parameter limit, Oracle command configuration và trạng thái connection; đây là các guard có giá trị.
- Cache in-memory dùng compare-and-remove khi xóa entry hết hạn; source Json phân biệt selection và conversion, có fast path Int64/UInt64 trước parser số rộng.

## 13. Kết quả tái kiểm chứng các lỗi Core/gRPC

Không chỉ đọc diff: đã build Core/gRPC và hai generator từ HEAD, rồi dùng cùng các chương trình regression trước đây.

| Nhóm cũ | Package mới | Source HEAD | Bằng chứng |
|---|---|---|---|
| CORE-01..04 | Còn | Còn | Covariance `InvalidCastException`; `ICommand` không tìm thấy handler; hai notification interface chỉ chạy một; order/attribute semantics không đổi |
| CORE-05..06 | Còn | Còn | Mapper tạo null cho required parameter; yêu cầu HTTP 202/204 vẫn trả 200 ở nhánh được thử |
| CORE-07..10 | Còn | Còn | Stop chưa drain execution; `NullReferenceException` khi job kết thúc; invalid schedule fallback; history-only equality và lỗi ghi bị nuốt |
| GRPC-01 | Còn | Còn | Repeated/map trên protobuf response thành rỗng ở cả client và server |
| GRPC-02 | Còn | Còn | `SERVER_CONVERSION` và `CLIENT_CONVERSION` cho CS0029 |
| GRPC-03 | Còn | Còn | `CLIENT_EMPTY` cho CS1526 |
| GRPC-04 | Còn | Còn | `PARTIAL_CONSTRUCTOR` cho CS7036 |
| GRPC-05..07 | Còn | Còn | Core validation thành Internal; provider dispose hai lần; nested context không phục hồi |
| GRPC-08 | Bảng chứng lịch sử của Grpc 2.0.0 | Chưa tái chạy biến thể thiếu global using | Không đánh dấu đã sửa hoặc đã kiểm chứng |

**Tổng:** 17 nhóm đã tái kiểm chứng trong lượt cập nhật, 1 nhóm giữ bằng chứng lịch sử. Không cộng số compiler vào nhóm root-cause.

Switch/case CQRS vẫn không phải vấn đề chính. Type-pattern switch không tự động là jump table O(1); dictionary thêm lookup/indirection và cũng không giải quyết covariance hoặc discovery. Có thể giữ switch nhỏ, hoặc generated typed executor theo đường nóng; quyết định bằng benchmark cùng semantics, số handler và pipeline thực tế.

## 14. Finding mới và giải pháp logic

Ký hiệu bằng chứng:

- **R:** đã chạy probe trên cả NuGet và source HEAD.
- **S:** đọc source HEAD, đối chiếu decompiled DLL; chưa có integration test hạ tầng thật.
- **D:** giới hạn thiết kế/contract, không tự động là bug trái tài liệu.

P1/P2 bên dưới là ưu tiên sửa tính đúng đắn/vận hành, không phải severity bảo mật. Các mục này không được gộp thành tổng "lỗi đã chứng minh" vì trộn bằng runtime, source và giới hạn thiết kế.

### ECO-01 — P1 / R: DataSession và IUnitOfWork không chia sẻ transaction

**Nguồn:** `DataServiceCollectionExtensions.cs`, `DataSession.cs`, `Repository.cs`, `AdoNetStoreSupport.cs`.

DI tạo `IUnitOfWork` và `DataSession` riêng; repository mới chạy qua `DataSession`, còn durable outbox chỉ đọc `IUnitOfWork.CurrentTransaction`.

**Quan sát:** khi DataSession đang có transaction, `CurrentTransaction` của IUnitOfWork vẫn null. Khi mở IUnitOfWork rồi ghi bằng DataSession trên cùng SQLite shared-memory database, nhận `SqliteErrorCode=6` do connection/transaction thứ hai bị khóa. Control dùng outbox cùng IUnitOfWork rồi rollback trả 0 pending; transaction DataSession riêng cũng rollback đúng.

**Ý nghĩa:** các thành phần riêng lẻ hoạt động, nhưng ghép cùng scope DI vẫn có business write + outbox atomicity. Trên DB khác có thể là transaction độc lập thay vì lỗi lock; chưa chạy để khẳng định biểu hiện đó.

**Giải pháp:** chọn một scoped transaction owner. Hoặc adapter outbox dùng chính connection/transaction lease của DataSession, hoặc IUnitOfWork ủy quyền hoàn toàn cho DataSession. Ownership/disposal phải duy nhất; không "sửa" bằng cách tự mở connection mới. Viết integration test: business insert + outbox save, throw trước commit: cả hai cùng biến mất; commit: cả hai cùng tồn tại. Sửa README mẫu transaction sau khi contract thống nhất.

### ECO-02 — P1 / R: Worker hết lease vẫn sửa trạng thái claim mới

**Nguồn:** `IOutboxStore.cs`, `AdoNetOutboxStore.cs`.

Claim có `LockedUntilUtc`, nhưng `MarkProcessed`/`MarkFailed` chỉ nhận message ID. Không có token/version phân biệt hai lần claim.

**Tái hiện có clock điều khiển:** A claim 1 giây → advance 2 giây → B claim 10 giây → A gọi MarkFailed muộn → C claim lại được ngay, dù lease B còn hạn. Cả in-memory lẫn SQLite đều cho 1 message cho C.

**Giải pháp:** claim trả `(Message, ClaimToken, LockedUntil)`; cập nhật trạng thái với `WHERE Id=@id AND ClaimToken=@token AND ProcessedAtUtc IS NULL`; affected rows=0 nghĩa là stale owner, không ghi đè. Renew lease có cùng fencing; batch tuần tự cần giới hạn kích thước/renew để các message cuối batch không hết lease trước khi xử lý. Fencing bảo vệ trạng thái store, không loại trừ duplicate publish sau crash: consumer vẫn cần idempotency.

### ECO-03 — P1 / R: Outbox relay không tự truyền MessageId ổn định

**Nguồn:** `OutboxMessage.cs`, `OutboxPublisherService.cs`, `RabbitMqProducer.cs`.

Với message tạo bằng headers mặc định, relay thêm `linkit-partition-key=Outbox.Id` nhưng không thêm `linkit-message-id`. Fake producer nhận `MessageId=False, PartitionKey=True`; Rabbit producer chỉ gán AMQP MessageId nếu header tương ứng tồn tại.

**Ảnh hưởng:** consumer dựa vào MessageId không có identity mặc định ổn định cho dedup. Không kết luận mọi ứng dụng bị lỗi: caller có thể chủ động cung cấp header. Partition key phục vụ grouping/ordering, không nên bị đồng nhất với event identity.

**Giải pháp:** tạo/persist MessageId từ Outbox.Id ngay khi lưu hoặc relay điền nếu thiếu; bảo toàn qua retry/replay. Xác định policy nếu caller cung cấp MessageId khác. Test hai lần publish cùng row có cùng identity; hai event khác nhau cùng partition vẫn khác identity.

### ECO-04 — P2 / R: In-memory inbox có composite-key collision

**Nguồn:** `InboxStore.cs`.

Key ghép `consumerName + ":" + messageId`: tuple `("a", "b:c")` trùng `("a:b", "c")`. Probe mark tuple đầu, lookup tuple sau trả true. Durable inbox dùng composite key thực và cùng phép thử trả false.

**Giải pháp:** dùng `ConcurrentDictionary<(string Consumer, string MessageId), ...>` hoặc key struct; không ghép bằng delimiter không escape. Bổ sung test parity giữa in-memory và durable store. Việc HasProcessed rồi Mark riêng rẽ vẫn không chống concurrent duplicate execution; đây là giới hạn đã được README nêu, cần atomic claim hoặc unique constraint + transaction toàn bộ business operation.

### ECO-05 — P2 / R: Cache invalidation không vô hiệu hóa factory đang chạy

**Nguồn:** `CacheProvider.cs`.

Probe chặn factory bằng TaskCompletionSource, gọi Remove, sau đó cho factory hoàn tất: giá trị 42 được ghi lại. Remove chỉ xóa cache, không đổi generation in-flight work.

**Giải pháp:** generation/version per key; factory chỉ publish nếu generation còn đúng. Remove và Set mới làm mất hiệu lực factory cũ nếu contract yêu cầu write-after-invalidate không được hồi sinh dữ liệu. Tránh chỉ cancel factory: I/O có thể không trọn token. Nếu rõ weak/strong invalidation semantics, test cả Remove và Set chen giữa.

Hai edge case khác đã quan sát: cached null gọi lại factory hai lần; đúng thời điểm TTL expiry vẫn đọc được giá trị vì điều kiện `>` thay vì `>=`. Quyết định và tài liệu hóa null caching; dùng hit flag độc lập với value, chuẩn hóa boundary TTL và validate TTL âm/zero theo contract đã chọn.

### ECO-06 — P2 / R: Hybrid cache coi 0/false là cache miss

**Nguồn:** `HybridRedisCacheProvider.cs`, đối chiếu `CacheProvider.cs`.

Tắt Redis để có lớp L1: gọi GetOrCreate cùng key, factory trả 0 hai lần → factory chạy 2 lần. InMemoryCacheProvider cùng value giữ single-flight/cache hit và chỉ chạy 1 lần, kể cả kiểm tra lại sau khi tắt.

**Nguyên nhân:** Hybrid dùng `value != default(T)` để phân biệt hit/miss và bỏ qua Set khi kết quả là default; default interface implementation cũng có cách kiểm tra này.

**Giải pháp:** contract đọc trả `(Found, Value)` hoặc cache entry có marker, không suy luận từ giá trị. Áp dụng cùng semantics ở interface, L1 và L2; test 0, false, `Guid.Empty`, null, giá trị bình thường và key sai type.

### ECO-07 — P1 / S: Hybrid L1 có thể giữ dữ liệu lâu hơn TTL của Redis

**Nguồn:** `HybridRedisCacheProvider.cs`.

L2 hit được đưa vào L1 với `DefaultTtl`, không lấy remaining TTL. Ví dụ Redis key còn 1 giây, DefaultTtl 10 phút: node mới có thể giữ giá trị gần 10 phút. Set/Remove ở node A cũng không invalidation L1 của node B.

**Giải pháp:** xác định consistency budget; đưa expiry vào cache envelope hoặc lấy value + remaining TTL một cách nhất quán; TTL L1 không vượt remaining TTL nếu contract yêu cầu expiry toàn hệ thống. Với invalidation đa node, dùng versioned key hoặc cơ chế thông báo kèm sự kiện mất/stale; pub/sub đơn thuần không bảo đảm tuyệt đối. Nếu chủ động eventual consistency, công bố rõ cửa sổ stale thay vì gọi hybrid là cache đồng bộ.

**Giới hạn bằng chứng:** đã xác nhận logic ở source và DLL, chưa chạy Redis đa node/TTL integration.

### ECO-08 — P1/P2 / S: RESP2 client thiếu error, cancellation và operation-timeout contract

**Nguồn:** `Resp2RedisClient.cs`, `HybridRedisCacheProvider.cs`.

- AUTH/SELECT đọc rồi bỏ qua response; không xác nhận `+OK`.
- DEL trả true sau ReadLine, không kiểm tra integer reply hay Redis error.
- Get/Set và Hybrid có broad catch biến lỗi/cancellation/serialization thành miss hoặc thành công phía L1 mà không logging/metric.
- ConnectTimeout chỉ bao quanh connect/handshake; read/write sau đó phụ thuộc caller token. Factory shared dùng `CancellationToken.None` nên cần operation timeout độc lập.
- MaxPoolSize giới hạn idle pool, không giới hạn active connections; Dispose không ngăn connection đang mượn quay lại pool.

**Giải pháp:** parse RESP thành typed reply, reject/error rõ; giữ caller cancellation; tách chính sách fail-open có observability khỏi lỗi programming/serialization. Mỗi operation có deadline; semaphore/bulkhead giới hạn concurrency; disposed flag và ownership cleanup đầy đủ. AUTH/SELECT thất bại phải dispose connection, không đưa vào pool.

**Lựa chọn kỹ thuật:** ưu tiên adapter Redis client đã trưởng thành nếu không có benchmark/AOT requirement cụ thể buộc tự viết. Nếu giữ custom RESP2 thì phải chấp nhận chi phí kiểm thử fragmentation, EOF, reconnect, negative response, pool drain và cancellation; "nhỏ, không reflection" chưa đủ để kết luận hiệu quả nhất. Chưa kiểm thử Redis thật nên đây là finding source-level, không phải lỗi production đã quan sát.

### ECO-09 — P2 / R: Tenant thiếu header kế thừa ambient context hiện có

**Nguồn:** `Tenancy.cs`.

Probe đặt tenant ngoài `"outer"`, Enter message không header → bên trong vẫn thấy `"outer"`. Có header `"inner"` thì vào inner và dispose phục hồi outer đúng. Với custom `ITenantAccessor` không phải `TenantAccessor`, interceptor trả null.

**Giải pháp:** mỗi message phải có policy: reject tenant bắt buộc, hoặc tạo context neutral mới; sau đó restore previous scope. Bổ sung scope API vào abstraction thay vì cast concrete accessor. Chưa có bằng chứng cross-tenant data access trong ứng dụng thực; đây là lỗ hổng đã khai thác, không gọi là… (finding thiết kế). Data tenant resolver và cache-key namespace vẫn cần wiring riêng.

### ECO-10 — P2 / R: Cancellation mặc định của Resilience không khớp XML contract

**Nguồn:** `ResiliencePipeline.cs`.

XML của `ShouldRetry` nói mặc định loại cancellation; code chỉ loại `OperationCanceledException` khi caller token đã cancel. Action trả canceled task bằng token độc lập, `MaxRetries=2` → 3 attempts.

**Giải pháp:** mặc định không retry `OperationCanceledException`, ngoại trừ timeout được phân loại riêng theo policy rõ; cho application opt-in nếu có nhu cầu đặc biệt. Chỉ đếm failure phù hợp vào breaker, không coi mọi validation/cancellation là dependency failure.

**Timeout cần hiểu đúng:** probe đợi token timeout đã cancel rồi trả 17; pipeline vẫn trả 17. Triển khai hiện tại là cooperative cancellation + exception translation, không có deadline cưỡng bức cho await. README đã có cảnh báo cooperative, nhưng cần tránh câu chữ ngụ ý pipeline chắc chắn kết thúc đúng hạn. Nếu thêm bounded wait, phải observe task còn chạy và tránh retry lúc side effect còn tồn tại. Không coi việc không cưỡng bức arbitrary task là một bug riêng.

### ECO-11 — P1/P2 / S: RabbitMQ còn các khoảng trống delivery và backpressure

**Nguồn:** `RabbitMqProducer.cs`, `RabbitMqConnection.cs`, RabbitMQ publisher confirms.

- Publish không bật mandatory/không xử lý returned message. Broker confirm không bảo đảm có queue đích, cũng không phải consumer acknowledgment; relay có thể mark processed mà ứng dụng vẫn chưa bảo đảm routing.
- Cache exchange dùng `OrdinalIgnoreCase`, nhưng exchange names phân biệt hoa thường. Cần Ordinal và xử lý conflict type khi cùng tên.
- `ProduceAsync` gọi `BasicPublish` và `WaitForConfirmsOrDie` 5 giây đồng bộ; `CancellationToken` không được dùng. Pool chỉ cap idle channels, không phải concurrency limit.
- Consumer mặc định requeue khi lỗi: cần bounded retry/DLX để không poison loop; shutdown đóng channel mà không có cơ chế drain handler tương minh trong đường đọc.

**Giải pháp:** xác định contract `ProduceAsync` là accepted/routed/persisted đến đâu; với routed-delivery requirement, mandatory + return handling + confirm correlation trước khi thành công. Giới hạn số publish in-flight; batch/asynchronous confirms nếu benchmark cần. Thêm test exchange case, unroutable message, confirm timeout, canceled publish và shutdown trong lúc handler chạy.

**Giới hạn:** chưa có broker thật, nên chưa khẳng định throughput, shutdown outcome qua network hoặc tổn thất message đã xảy ra.

### ECO-12 — D: Sagas cần được định vị đúng

**Nguồn:** `Sagas.cs` và README package.

Probe cùng correlation ID và cùng event chạy step hai lần → 2 side effects. Repository có Find/Save, không expected version, event ID hay durable transition log. Đây phù hợp giới hạn đã công bố, không phải chứng minh package thất hứa exactly-once.

Nếu mục tiêu là production orchestration, cần state-machine transition guard, CAS/version, unique `(SagaId, EventId/StepId)`, durable compensation status/retry, outbox đi kèm transaction state, và recovery sau crash giữa remote side effect và save. Compensate hiện chỉ áp dụng step đang chạy, không tự đi ngược toàn bộ các step đã thành công. Nếu không định làm những phần này, giữ scope nhỏ và ghi rõ là step coordinator.

## 15. Tối ưu hiệu năng và best practice

### 15.1. Checklist bổ sung cho cache mới

Đây là scan có chủ đích của 3 file source HEAD: `CacheProvider`, `HybridRedisCacheProvider`, `Resp2RedisClient`. Không cộng decompiled code, generated state machine hay toàn bộ ecosystem vào số đếm. Đếm theo dòng khớp, không phải số allocation. Checklist dùng trước khi phân loại các đề xuất hiệu năng bên dưới.

| Recipe | Hit |
|---|---|
| IndexOf literal / StartsWith-EndsWith-Contains | 0/0 |
| Substring / ToLower-ToUpper / Replace | 1/0/0 |
| params / new byte[] | 2/2 |
| LINQ Select-Where-OrderBy-GroupBy / Any-All | 1/0 |
| new Dictionary-List / static readonly Dictionary | 0/0 |
| RegexOptions.Compiled / new Regex / GeneratedRegex | 0/0/0 |
| Unsealed class / sealed class, gồm nested | 2/6 — sealed 6/8 |
| IEquatable | 0 |
| async void / .Result-.Wait / Task.Run | 0/0/0 |
| string.Format | 0 |
| await / ConfigureAwait | 45/45 |

Zero explicit `new Dictionary` không có nghĩa zero allocation: source dùng target-typed `new`, và scan đã được đọc ngữ cảnh. 6/8 class sealed không có nghĩa hai class còn lại phải bị seal. Không đề xuất FrozenDictionary cho cache mutable; không thay Task bằng ValueTask hàng loạt.

### 15.2. Đề xuất hiệu năng có căn cứ, chưa có số đo

**PERF-ECO-01 — 🟡 Moderate: RESP reader đọc từng byte**
- Impact: `ReadLineAsync` tạo `MemoryStream`/`byte[1]`, await `ReadAsync` từng byte, rồi `ToArray` và decode; đáng tối ưu nếu Redis là hot path.
- File: `Resp2RedisClient.cs`, phương thức `ReadLineAsync`.
- Fix: buffered parser với cursor dùng lại; giữ dư dữ liệu cho reply tiếp theo, giới hạn frame và kiểm thử fragmentation.
- Caveat: không tuyên bố mỗi `ReadAsync` là một syscall hoặc nhanh hơn bao nhiêu khi chưa đo.

**PERF-ECO-02 — 🟡 Moderate: Compound serialization của RESP writer**
- Impact: một SET đi qua encode args, params array, MemoryStream, nhiều `WriteAscii` allocations, `ToArray` rồi NetworkStream; giảm dependency không đồng nghĩa giảm allocation.
- File: `Resp2RedisClient.cs`, `WriteCommandWithRawArgAsync` và `WriteAscii`.
- Fix: một buffer writer tái dùng có ownership rõ; encode length/arguments trực tiếp và một lần write khi phù hợp.
- Caveat: không đếm cố định số allocation cho mọi input; benchmark payload nhỏ/lớn, concurrency và pool.

**PERF-ECO-03 — ℹ️ Info: Eviction duyệt cache và sắp thứ tự LRU**
- Impact: đường capacity pressure duyệt expired entries rồi `OrderBy/Take`; chỉ tăng theo lượng entry.
- File: `CacheProvider.cs`, `EvictToMakeRoom`.
- Fix: do trước cần nhắc sampled eviction/approximate LRU hoặc cache provider chuẩn nếu eviction vào hot path.
- Caveat: không hứa hard cap bằng `ConcurrentDictionary.Count`; README hiện đã ghi best-effort. LINQ `OrderBy/Take` có thể là runtime tối ưu, không khẳng định luôn sort toàn bộ.

**Điểm tích cực:** không thấy sync-over-async hay async void trong 3 file; 45/45 dòng await có `ConfigureAwait`; in-memory có compare-and-remove và single-flight. Cross-file check phát hiện Hybrid không giữ semantics giá trị 0 như InMemory, đã ghi riêng ở ECO-06 là correctness issue.

### 15.3. Benchmark cần có trước khi gọi là "tối ưu nhất"

| Đường chạy | Ma trận tối thiểu | Đo gì |
|---|---|---|
| CQRS | 1/10/100/1000 handler; first/middle/last; 0/1/5 behavior; sync-completed vs async | Latency, allocations, code size, cold start; switch vs typed executor vs dictionary cùng semantics |
| Data | Generated reader/AST vs Dapper/EF no-tracking; projection, join, paging, streaming | SQL round trips, throughput, p95/p99, allocations; cùng DB/index/result set |
| Cache | L1 hit, L2 hit, miss storm, default/null, invalidate race; 1/32/128 concurrency | Factory count, Redis QPS, stale window, allocations, active connection peak |
| Outbox + RabbitMQ | Confirmed publish, unroutable, retry storm, expired lease, process crash | Delivery lag, duplicate rate, loss invariant, batch duration so với lease, backlog growth |
| Json | Parse path mỗi lần vs reuse; scalar/wildcard/nested; JsonElement vs JsonNode; integer extremes | ns/op, allocated bytes/op, enumeration behavior; chỉ so cùng semantics |
| AOT | Một app tiêu biểu từng provider/serializer và RID deployment thật | Publish warnings, runtime smoke, startup/size; không suy ra từ reflection ở một class |

Với Json, đường JsonNode fallback serialize rồi parse lại là candidate allocation cần đo, không phải bug mặc định. JsonElement selection có lifetime gắn với document; conversion clone phải test riêng. Các probe hiện tại chưa chứng nhận toàn bộ grammar, Unicode, overflow, fuzz và serializer metadata.

## 16. Lộ trình sửa đề xuất và acceptance criteria

### Giai đoạn 1 — Correctness và contract trước release tiếp theo

1. Core/gRPC: xử lý 17 nhóm tái hiện; bổ sung lại GRPC-08 vào compiler suite. Giữ nguyên public semantics hoặc công bố breaking change.
2. Thống nhất DataSession/IUnitOfWork/Outbox transaction owner; rollback business + event cùng lúc.
3. Outbox: claim token/fencing, identity ổn định, stale-completion test, metric lease overrun.
4. Cache: Found/Value contract; invalidation generation; TTL/null/default parity; xác định consistency hybrid.
5. Redis/RabbitMQ: error/cancellation/delivery contract có observability, không silent success.

### Giai đoạn 2 — Integration suite và giới hạn vận hành

- Chạy consumer application test từ `.nupkg`, không chỉ ProjectReference.
- SQLite bắt buộc trong CI; PostgreSQL/SQL Server/MySQL/Oracle là jobs riêng có DB thật. Job bỏ qua do thiếu connection string **không được báo như provider đã pass**.
- RabbitMQ/Redis disposable test services do test runner quản lý; không dùng production broker/cache.
- Dùng controlled clock/barrier cho lease, expiry, cancellation; fault injection cho commit outcome, publish-before-mark, reconnect và partial frame.
- Kiểm tra tenant header thiếu/đúng/custom accessor; cache key bao gồm tenant khi dữ liệu có partition theo tenant.
- Saga có scope contract tests: duplicate event, concurrent transition, crash sau side effect, compensation failure.
- Production mặc định nên opt-in schema DDL/migrations, có deploy gate; Data hiện bật `EnsureDatabaseCreated`/`AutoSyncSchema`/`RunMigrations` mặc định. Không chạy provisioning đặc quyền cùng app chỉ vì là default tiện dụng.

### Giai đoạn 3 — Tối ưu theo profile và giữ phạm vi sản phẩm

Benchmark các đường ở mục 15, chọn thay đổi theo workload. Có thể dùng EF/Dapper/Redis client/Polly hoặc orchestration engine trưởng thành làm adapter khi cần tính năng rộng; không nhất thiết tự xây lại mọi lớp để giữ ecosystem "thuần LinKit".

**Release gate đề xuất:** các P1 đã có regression đỏ trước sửa, xanh sau sửa; package smoke pass; build/AOT support được công bố theo ma trận thực chạy; version/source provenance nhất quán; docs không dùng từ "100%" ngoài phạm vi đã chứng minh.

## 17. Bằng chứng thực thi và giới hạn của lượt cập nhật

### 17.1. Môi trường và phương pháp

- Windows, SDK 10.0.402, Release/net10.0; không khẳng định đã chạy net8/net9.
- Probe NuGet pin 11 version theo mục 11; probe source tham chiếu 11 project của HEAD cùng thời điểm.
- SQLite driver dùng Microsoft.Data.Sqlite 10.0.14, database in-memory có tên ngẫu nhiên, không truy cập DB production. Phiên bản 10.0.0 thử ban đầu có cảnh báo NU1903 ở dependency test; đã đổi driver probe sang 10.0.14 và lần restore/build cuối không có warning. Đây không phải dependency do LinKit tự kéo vào.
- Fake producer capture headers; gRPC fake invoker/context; không có live RabbitMQ hoặc Redis.
- Main package/source graph và positive Core/gRPC probes build thành công; negative compiler cases cố ý thất bại, sau đó baseline source được build thành công.
- Chỉ cập nhật báo cáo trong workspace; source tải về và probe nằm trong session, không sửa implementation/thêm package vào project LinKit.Json của người dùng.

### 17.2. Output của cùng probe trên NuGet và HEAD

Hai lượt chạy cho cùng giá trị sau:

```text
CACHE single-flight including zero: calls=1
CACHE hybrid zero result factory calls=2
CACHE null result factory calls=2
CACHE exact TTL boundary value=7
CACHE value resurrected after Remove=42
TENANCY headerless message tenant=outer
TENANCY explicit message tenant=inner
TENANCY restored tenant=outer
RESILIENCE independent cancellation attempts=3
RESILIENCE successful value after timeout token canceled=17
SAGAS repeated event executions=2
INBOX delimiter collision=True
OUTBOX third claim while B lease active=1
OUTBOX relay message-id header=False, partition-key=True
OUTBOX.DATA rollback pending=0
OUTBOX.DATA third claim while B lease active=1
OUTBOX.DATA inbox delimiter collision=False
DATA session insert inside UoW blocked by separate transaction: SqliteErrorCode=6
DATA session transaction=True, outbox UoW transaction=False
DATA transaction rollback row count=0
JSON wildcard values=1,2
JSON null found=True, kind=Null
JSON missing found=False
JSON Int128 exact=True
JSON published package rejects relative path
```

Dòng cuối dùng cùng label trong cả hai executable; thực tế package và source HEAD đều yêu cầu `$`. Không lấy README hoặc benchmark của workspace Json khác làm chứng cứ thay cho package 1.0.0/HEAD này.

Các probe/source/log giữ tại session `files/ecosystem-review` và `files/eco-head`; evidence có tính cục bộ phiên làm việc, không phải CI artifact công khai. Main `Review.csproj` dùng NuGet; `SourceLatest/SourceLatest.csproj` dùng source; CoreRegression/GrpcRegression chọn source bằng `-p:UseSource=true`.

### 17.3. Điều chưa được chứng minh

Không có số benchmark, Native AOT publish, live broker/cache, full ORM correctness suite, full Json unit/fuzz suite hoặc test bốn DB ngoài SQLite trong lượt này. Không cho phép đo throughput/latency chứng minh "nhanh nhất". Build 11 project không đồng nghĩa test mọi tính năng của cả 11 thư viện.

### Tổng kết bổ sung về performance

| Severity | Count | Top Issue |
|---|---|---|
| 🔴 Critical | 0 | Chưa đo được regression hiệu năng nghiêm trọng |
| 🟡 Moderate | 2 | Redis line reader và compound writer allocation trên hot path tiềm năng |
| ℹ️ Info | 1 | Cache eviction khi capacity pressure |

Bảng này chỉ tính 3 đề xuất performance mới, không cộng correctness findings, không cộng bảng performance lịch sử. Correctness và giới hạn thiết kế được phân loại riêng ở mục 14.

**Kết luận sau kiểm tra lại source:** LinKit đã phát triển thêm đáng kể và có những guard/best practice tốt. Nhưng source mới nhất không đồng nghĩa những lỗi cũ đã được sửa; các đường đã tái thử vẫn cho kết quả tương tự package. Ưu tiên tiếp theo nên là chất lượng ranh giới giữa module và release discipline, không phải bỏ switch CQRS hay tiếp tục tăng số feature.

> Đánh giá có AI hỗ trợ, có thể bỏ sót hoặc diễn giải sai trường hợp ngoài phạm vi thử. Probe xác nhận đúng các tình huống được nêu, không bảo đảm toàn ứng dụng. Các finding source-only cần integration test; mọi đề xuất tối ưu cần benchmark và human review trước khi áp dụng production.

