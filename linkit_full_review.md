# LinKit Library — Comprehensive Review & Recommendations

> Mục tiêu: Build ecosystem cho developer ít code, tập trung nghiệp vụ, code-gen driven, không reflection, tối ưu, AOT-ready.

---

## 🏗️ Tổng quan kiến trúc

```
LinKit.Core          — CQRS, Mapping, Endpoints, Messaging abstractions (no impl)
LinKit.Generator     — Roslyn Source Generator cho CQRS, DI, Endpoints, Mapper, Messaging, Background Jobs
LinKit.Data          — Repository, UnitOfWork abstractions + DB Attributes
LinKit.Data.Generator— Roslyn Source Generator cho Repository, Schema, Migration, Mapper từ DB entities
LinKit.Outbox        — Transactional Outbox pattern (in-memory default)
LinKit.Sagas         — Saga Coordinator + ISagaRepository (in-memory default)
LinKit.Resilience    — Attributes chỉ (RetryAttribute, CircuitBreaker, Timeout) — chưa có impl
LinKit.Caching       — (cần kiểm tra)
LinKit.Grpc          — gRPC support
LinKit.Messaging.*   — Kafka, RabbitMQ implementations
LinKit.Tenancy       — Multi-tenancy support
```

**Nhận xét chung:** Kiến trúc rất solid, tư duy đúng hướng. Code-gen hoàn toàn, không reflection trong hot path. Rất phù hợp cho AOT.

---

## 🐛 Bugs & Lỗi tiềm ẩn

### 1. [CRITICAL] `DataGenerator` — DataReader mapping theo ordinal (vị trí) thay vì tên cột
**File:** `LinKit.Data.Generator/DataGenerator.cs` L509–619

```csharp
// Hiện tại: map theo ordinal index i
sb.Append($"        if (!reader.IsDBNull({i})) ");
entity.{p.PropertyName} = ...reader.GetValue({i})...
```
**Vấn đề:** Nếu câu SQL `SELECT` trả về cột theo thứ tự khác với định nghĩa entity (do JOIN, alias, custom SQL), mapping sẽ **sai hoàn toàn** mà không báo lỗi.

**Fix đề xuất:** Map theo tên cột thay vì ordinal:
```csharp
// Safe approach
var ordinal_{p.PropertyName} = reader.GetOrdinal("{p.ColumnName}");
if (!reader.IsDBNull(ordinal_{p.PropertyName})) 
    entity.{p.PropertyName} = reader.GetValue(ordinal_{p.PropertyName});
```
Hoặc build một `Dictionary<string, int>` từ `reader.GetName(i)` sau khi execute.

---

### 2. [CRITICAL] `AttachParameters` — Dùng reflection fallback (`GetType().GetProperties()`)
**File:** `DataGenerator.cs` L902–910

```csharp
var props = parameters.GetType().GetProperties();
foreach (var prop in props)
    p.Value = prop.GetValue(parameters) ?? DBNull.Value;
```
**Vấn đề:** Dùng reflection — phá vỡ nguyên tắc AOT. Khi publish Native AOT, reflection trên anonymous object bị trim. NativeAOT sẽ throw `MissingMetadataException`.

**Fix:** Chỉ accept `IEnumerable<(string, object?)>` hoặc `Dictionary<string, object?>`. Bỏ reflection fallback, hoặc đánh dấu rõ là "not AOT safe" và document.

---

### 3. [HIGH] `ExpressionSqlTranslator` — `ExtractValue` dùng `Expression.Lambda(...).Compile().DynamicInvoke()`
**File:** `LinKit.Data/Linq/ExpressionSqlTranslator.cs` L418

```csharp
return Expression.Lambda(expr).Compile().DynamicInvoke();
```
**Vấn đề:** `Compile()` sinh IL code động + `DynamicInvoke()` dùng reflection. **Không hoạt động trên Native AOT.**

**Fix:** Cần `[DynamicDependency]` hoặc loại bỏ code path này. Thực tế, `ExtractValue` chỉ được gọi cho closures — nên chuyển sang `[RequiresDynamicCode]` hoặc yêu cầu dev truyền value trực tiếp qua parameter.

---

### 4. [HIGH] `InsertRangeAsync` / `UpdateRangeAsync` / `DeleteRangeAsync` — N+1 không có batch
**File:** `DataGenerator.cs` L1058–1161

```csharp
// InsertRangeAsync gọi InsertAsync trong loop — mỗi entity = 1 roundtrip DB
foreach (var item in entities)
    await InsertAsync(item, ct);
```
**Vấn đề:** 1000 entities = 1000 roundtrips. Với SQL Server có thể dùng `Table-Valued Parameters` hoặc bulk insert.

**Fix:** Implement batch insert với multi-row VALUES:
```sql
INSERT INTO "Table" (col1, col2) VALUES (@p0, @p1), (@p2, @p3), ...
```

---

### 5. [HIGH] `UnitOfWork` — Không thread-safe, không giải phóng connection khi không có transaction
**File:** `LinKit.Data/UnitOfWork.cs`

```csharp
_connection ??= await _factory.CreateConnectionAsync(ct);
```
**Vấn đề 1:** UoW giữ connection suốt lifetime của scope. Nếu không có transaction, connection vẫn bị giữ.
**Vấn đề 2:** Nếu `BeginTransactionAsync` được gọi đồng thời từ nhiều thread (dù hiếm), có race condition.

---

### 6. [HIGH] `SagaCoordinator` — `InMemorySagaRepository` dùng `Singleton` nhưng mất data khi restart
**File:** `LinKit.Sagas/Sagas.cs` L114

```csharp
services.AddSingleton<ISagaRepository<TData>, InMemorySagaRepository<TData>>();
```
**Vấn đề:** Document rõ là "chỉ dùng dev/test" — production phải implement durable `ISagaRepository`. Không có warning/guard nào.

---

### 7. [MEDIUM] `CqrsGeneratorPart` — `HandleVoidRequest` naming conflict khi nhiều void commands
**File:** `LinKit.Generator/Generators/CqrsGeneratorPart.cs` L542–545

```csharp
private Task HandleVoidRequest({h.RequestType} request, CancellationToken cancellationToken)
    => _serviceProvider.GetRequiredService<{h.HandlerInterface}>().HandleAsync(request, cancellationToken);
```
**Vấn đề:** Mỗi handler gen ra 1 method tên `HandleVoidRequest` hoặc `HandleResultRequest` — nhưng các method này có **parameter type khác nhau** và C# không cho overload phân biệt qua generic return type. Cần kiểm tra xem generated code có compile không khi có nhiều handlers cùng `void` return.

**Thực tế:** Compiler phân biệt qua `{h.RequestType}` param, OK — nhưng naming không descriptive. Đề xuất đổi sang `Handle_{SafeRequestTypeName}`.

---

### 8. [MEDIUM] `EndpointsGeneratorPart` — `NormalizeRoute` ném exception trong Source Generator
**File:** `LinKit.Generator/Generators/EndpointsGeneratorPart.cs` L548

```csharp
throw new InvalidOperationException($"Invalid route: {route}");
```
**Vấn đề:** Ném exception trong Roslyn Source Generator sẽ crash **toàn bộ generator**, không phải báo lỗi đẹp cho dev. Nên dùng `spc.ReportDiagnostic(...)` thay thế.

---

### 9. [MEDIUM] `MapperGeneratorPart` — Mapper dùng `CompilationProvider` thay `ForAttributeWithMetadataName`
**File:** `LinKit.Generator/Generators/MapperGeneratorPart.cs` L61–62

```csharp
var mapConfigsPerClass = mapperContexts
    .Combine(context.CompilationProvider)
    .Select(...);
```
**Vấn đề:** Kết hợp với `CompilationProvider` làm **toàn bộ incremental pipeline bị invalidate** mỗi khi có thay đổi nhỏ trong compilation. Rất chậm với project lớn.

**Fix:** Dùng `SemanticModel` đã có sẵn trong `ForAttributeWithMetadataName` context thay vì kéo `CompilationProvider`.

---

### 10. [MEDIUM] `OutboxPublisherService` — Topic name derived từ `MessageType.Split('.').Last()`
**File:** `LinKit.Outbox/OutboxPublisherService.cs` L53

```csharp
string topic = msg.MessageType.Split('.').Last();
```
**Vấn đề:** `Namespace.OrderCreated` → topic `OrderCreated`. Nhưng `Namespace.OrderCreated<T>` → `OrderCreated<T>` là tên không hợp lệ cho Kafka/RabbitMQ. Cần sanitize và cho phép override topic.

---

### 11. [LOW] `CqrsGeneratorPart` — `GetCollectedBehaviors` tạo `scannedClassesFromContext` 2 lần
**File:** `LinKit.Generator/Generators/CqrsGeneratorPart.cs` L138

```csharp
public static void Initialize(IncrementalGeneratorInitializationContext context)
{
    var scannedClassesFromContext = GetScannedClassesFromContext(context); // Lần 2!
    // Nhưng GetServices() cũng đã gọi GetScannedClassesFromContext()
```
**Vấn đề:** `GetScannedClassesFromContext` được gọi tới 2 lần (trong `GetServices` và `Initialize`), tạo ra 2 pipeline streams độc lập, **không share cache**. Tốn tài nguyên khi incremental build.

**Fix:** Share pipeline duy nhất, truyền vào các methods.

---

### 12. [LOW] `FluentQuery.Where(string sqlCondition)` — SQL Injection risk
**File:** `LinKit.Data/Abstractions.cs` IFluentQuery interface

```csharp
IFluentQuery<TEntity> Where(string sqlCondition, object? parameters = null);
```
**Vấn đề:** `sqlCondition` là raw SQL string — nếu dev truyền user input vào đây là SQL injection. Cần doc rõ và/hoặc có mechanism validate.

---

### 13. [LOW] `GetEntitySemanticModel` — Scan chỉ `GetMembers()` (không scan inherited members)
**File:** `DataGenerator.cs` L181

```csharp
foreach (var member in classSymbol.GetMembers().OfType<IPropertySymbol>())
```
**Vấn đề:** `GetMembers()` chỉ trả về members khai báo trực tiếp, **bỏ qua inherited properties** từ base class. Trái lại, Mapper generator dùng `GetSettableProps()` có traverse base types. Cần đồng nhất.

---

## ✅ Điểm mạnh (Keep)

| Feature | Đánh giá |
|---------|-----------|
| Mediator code-gen (switch pattern, no reflection) | ⭐⭐⭐⭐⭐ |
| Pipeline behavior chaining (closure-based, no reflection) | ⭐⭐⭐⭐⭐ |
| Repository code-gen với multi-dialect SQL | ⭐⭐⭐⭐ |
| Mapper code-gen với ForMember/Ignore/ConvertWith | ⭐⭐⭐⭐ |
| Endpoint code-gen với auth, filters, OpenAPI | ⭐⭐⭐⭐⭐ |
| Outbox pattern tích hợp | ⭐⭐⭐⭐ |
| ExpressionSqlTranslator cho LINQ queries | ⭐⭐⭐⭐ |
| Multi-PK type support (Guid, int, long) | ⭐⭐⭐⭐ |
| JSON column support với AOT SerializerContext | ⭐⭐⭐⭐⭐ |
| Enum to/from int/string | ⭐⭐⭐⭐ |

---

## 🚀 Đề xuất nâng cấp (New Features)

### Tier 1 — Critical (Nên làm ngay)

#### A. `IValidator<TRequest>` Code-gen Pipeline Behavior
```csharp
// Dev chỉ cần implement:
[CqrsBehavior(typeof(ICommand), order: -10)]
public class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    // Auto-discovered, validator được inject
}

// Và define validator:
public class CreateOrderValidator : IValidator<CreateOrderCommand>
{
    public IEnumerable<ValidationError> Validate(CreateOrderCommand cmd) { ... }
}
```
**Generator** tự tạo `ValidationBehavior` và `IValidator<T>` registration.

---

#### B. `[BulkInsert]` / `[BulkUpdate]` cho Repository
```csharp
// Generated method dùng multi-row VALUES thay vì N+1:
public async Task InsertRangeAsync(IEnumerable<ProductEntity> entities, ...)
{
    // Batch size 1000
    // Generates: INSERT INTO "Products" (col1, col2) VALUES (@p0,@p1),(@p2,@p3),...
}
```

---

#### C. `[CompositeIndex]` attribute cho multi-column indexes
```csharp
[CompositeIndex("IX_Category_Name", nameof(CategoryId), nameof(Name), IsUnique = true)]
public class ProductEntity { ... }
```

---

#### D. Saga Persistence Generator
```csharp
[SagaEntity(typeof(OrderSagaData))]
public interface IOrderSagaRepository : ISagaRepository<OrderSagaData> { }
```
Generator tạo `OrderSagaDataRepository` dùng `LinKit.Data` infrastructure.

---

### Tier 2 — High Value (Nên làm sớm)

#### E. `[CqrsHandler]` auto-scan (bỏ attribute boilerplate)
Thay vì bắt dev đánh `[CqrsHandler]` trên mỗi handler, generator tự detect class implement `ICommandHandler<>` / `IQueryHandler<>`:
```csharp
// Không cần [CqrsHandler] nữa!
public class CreateProductHandler : ICommandHandler<CreateProductCommand> { ... }
```

---

#### F. Result/Error pattern tích hợp sẵn
```csharp
// Trong LinKit.Core:
public record Result<T>
{
    public static Result<T> Ok(T value) => new SuccessResult<T>(value);
    public static Result<T> Fail(string code, string message) => new FailureResult<T>(code, message);
}

// Endpoint generator tự handle Result<T>:
// - Ok(value) → 200
// - FailureResult với code "NotFound" → 404
// - FailureResult với code "Conflict" → 409
```

---

#### G. `[MultiTenantQuery]` auto-filter
```csharp
[Table("Products")]
[MultiTenant] // Auto-thêm WHERE TenantId = @currentTenantId vào mọi query
public class ProductEntity { ... }
```

---

#### H. `[Soft Delete]` support
```csharp
[SoftDelete(nameof(DeletedAt))] // Cột nullable DateTime
public class ProductEntity
{
    public DateTime? DeletedAt { get; set; }
}
// DeleteAsync → UPDATE SET DeletedAt = NOW()
// ListAsync → WHERE DeletedAt IS NULL auto-filter
```

---

#### I. Query Cache Behavior code-gen
```csharp
[ApplyBehavior(typeof(CacheBehavior<,>))]
[Cache(ttlSeconds: 300, key: "products:{request.CategoryId}")]
public record GetProductsQuery(Guid CategoryId) : IQuery<List<ProductDto>>;
```

---

### Tier 3 — Nice to have

#### J. OpenAPI tags từ code comments
Generator đọc XML doc comment và inject vào `WithSummary()`, `WithDescription()`.

#### K. `[Audit]` behavior tự động
```csharp
[CqrsBehavior(typeof(ICommand))]
public class AuditBehavior<TRequest, TResponse> { ... }
// Auto-log ai gọi command gì lúc mấy giờ
```

#### L. Health check generator
```csharp
// Auto-gen HealthCheck cho tất cả DB connections được register
services.AddLinKitHealthChecks(); // Từ generator
```

---

## 📋 Checklist best practices

| Item | Status | Note |
|------|--------|------|
| No reflection in hot path | ✅ | Trừ `AttachParameters` fallback và `ExtractValue` |
| AOT-compatible | ⚠️ | `Compile().DynamicInvoke()` và reflection fallback |
| Incremental generator performance | ⚠️ | `CompilationProvider` trong MapperGeneratorPart |
| SQL injection protection | ⚠️ | `IFluentQuery.Where(string)` cần doc |
| Thread safety | ⚠️ | UnitOfWork single-thread by design, cần doc |
| Batch operations | ❌ | InsertRange/UpdateRange/DeleteRange là N+1 |
| Proper DI scoping | ✅ | Handlers = Transient, Mediator = Scoped |
| Error reporting in generator | ⚠️ | `throw` thay vì `ReportDiagnostic` |
| XML documentation | ⚠️ | Nhiều nơi còn thiếu `<summary>` |
| Unit tests | ❌ | Thư mục Test tồn tại nhưng không rõ coverage |

---

## 🎯 Thứ tự ưu tiên fix

1. **[Ngay]** Fix DataReader mapping theo tên cột (Bug #1) — dễ, impact cao
2. **[Ngay]** Bỏ reflection fallback trong `AttachParameters` (Bug #2) — AOT blocker  
3. **[Ngay]** Fix `throw` → `ReportDiagnostic` trong `NormalizeRoute` (Bug #8) — DX
4. **[Sớm]** Implement batch insert/update/delete (Bug #4) — performance
5. **[Sớm]** Fix `CompilationProvider` trong MapperGeneratorPart (Bug #9) — build perf
6. **[Sớm]** Mark/replace `Expression.Lambda.Compile()` (Bug #3) — AOT compliance
7. **[Plan]** `[SoftDelete]`, `[MultiTenant]`, Result pattern, Cache behavior
