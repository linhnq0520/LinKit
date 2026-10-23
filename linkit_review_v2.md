# LinKit Ecosystem — Comprehensive Review v2.0

> **Mục tiêu:** Code-gen driven, zero-reflection hot path, AOT-ready, developer-first ecosystem.

---

## 📊 Tổng quan: Bugs v1 đã được fix

| Bug (v1) | Trạng thái |
|----------|------------|
| #1 DataReader mapping theo ordinal | ✅ **ĐÃ FIX** — dùng `GetOrdinal` + cache ordinals per result set |
| #2 Reflection fallback `AttachParameters` | ✅ **ĐÃ FIX** — throw `NotSupportedException` rõ ràng, AOT-safe |
| #3 `Expression.Lambda.Compile().DynamicInvoke()` | ✅ **ĐÃ FIX** — guard `RuntimeFeature.IsDynamicCodeSupported` |
| #4 InsertRange/DeleteRange N+1 | ✅ **ĐÃ FIX** — Batch INSERT + WHERE IN chunks |
| #5 UnitOfWork thread-safety | ✅ **ĐÃ FIX** — `SemaphoreSlim(1,1)` + proper async lock |
| #6 InMemorySagaRepository warning | ✅ **ĐÃ FIX** — XML doc `<remarks>WARNING: volatile...</remarks>` |
| #9 `CompilationProvider` trong MapperGeneratorPart | ✅ **ĐÃ FIX** — `ForAttributeWithMetadataName` + `SemanticModel` |

**Đánh giá chung hiện tại:** Ecosystem đã ở mức **production-ready** cho phần lớn use case. Còn edge cases, performance improvements, và missing features quan trọng.

---

## 🐛 Bugs & Lỗi tiềm ẩn MỚI

### 1. [HIGH] `EndpointsGeneratorPart` — `CreateSyntaxProvider` scan tất cả classes

**File:** [`EndpointsGeneratorPart.cs`](file:///e:/Workspace/My/LinKit/LinKit/LinKit.Generator/Generators/EndpointsGeneratorPart.cs#L119-L124)

```csharp
var endpointDeclarations = context.SyntaxProvider.CreateSyntaxProvider(
    predicate: (node, _) => (node is ClassDeclarationSyntax c && c.AttributeLists.Count > 0)
                         || (node is RecordDeclarationSyntax r && r.AttributeLists.Count > 0),
    transform: (ctx, _) => GetEndpointInfoFromSyntax(ctx)
).Where(info => info is not null);
```

**Vấn đề:** Predicate kích hoạt trên **mọi class/record có attribute** — với project lớn (hàng trăm classes), semantic model lookup xảy ra không cần thiết. `CreateSyntaxProvider` cũng invalidate pipeline kém hơn `ForAttributeWithMetadataName`.

**Fix:**
```csharp
// Tạo pipeline riêng cho mỗi attribute shorthand
var fromGet  = context.SyntaxProvider.ForAttributeWithMetadataName("LinKit.Core.Endpoints.HttpGetAttribute", ...);
var fromPost = context.SyntaxProvider.ForAttributeWithMetadataName("LinKit.Core.Endpoints.HttpPostAttribute", ...);
var fromApi  = context.SyntaxProvider.ForAttributeWithMetadataName("LinKit.Core.Endpoints.ApiEndpointAttribute", ...);
// Merge:
var all = fromGet.Collect().Combine(fromPost.Collect()).Combine(fromApi.Collect())...
```

---

### 2. [HIGH] `EndpointsGeneratorPart` — `routeGroups` kết hợp `CompilationProvider` → full rebuild mỗi keystroke

**File:** [`EndpointsGeneratorPart.cs`](file:///e:/Workspace/My/LinKit/LinKit/LinKit.Generator/Generators/EndpointsGeneratorPart.cs#L76-L117)

**Vấn đề:** `endpointDeclarations.Collect().Combine(routeGroups)` — `routeGroups` từ `CompilationProvider` invalidate khi compilation thay đổi → toàn bộ `Endpoints.g.cs` regenerate liên tục. Với 50+ endpoints, mỗi lần gõ phím rebuild toàn bộ.

**Fix ngắn hạn:** Tách `routeGroups` thành `IncrementalValueProvider` riêng được cache dựa trên nội dung assembly attributes, không toàn bộ compilation.

---

### 3. [HIGH] `CqrsGeneratorPart` — Double-cast `(Task<TResponse>)(object)` có thể throw `InvalidCastException`

**File:** [`CqrsGeneratorPart.cs`](file:///e:/Workspace/My/LinKit/LinKit/LinKit.Generator/Generators/CqrsGeneratorPart.cs#L478-L480)

```csharp
// Generated code:
ICommandX c => (Task<TResponse>)(object)HandleResultRequest(c, cancellationToken),
```

**Simulation Bug:**
```csharp
// Handler trả: Task<Result<OrderDto>>
// Dev gọi nhầm:
var result = await mediator.SendAsync<OrderDto>(command);
// → InvalidCastException: Cannot cast object of type 'Task`1[Result`1[OrderDto]]'
//   to type 'Task`1[OrderDto]'
```

**Fix:** Thêm `_ => throw new HandlerNotFoundException(...)` với message rõ ràng hơn, hoặc validate type match tại compile time nếu có thể.

---

### 4. [HIGH] `SagaCoordinator` — `step.GetType().Name` dùng reflection

**File:** [`Sagas.cs`](file:///e:/Workspace/My/LinKit/LinKit/LinKit.Sagas/Sagas.cs#L84)

```csharp
_logger?.LogInformation("Executing Saga step {Step}...", step.GetType().Name, ...);
```

**Vấn đề:** `GetType().Name` là reflection call. Trong AOT, dù thường không bị trim với concrete types, vẫn là AOT anti-pattern. Nếu `step` là interface, có thể fail.

**Fix:**
```csharp
public interface ISagaStep<TData, in TEvent> where TData : ISagaData
{
    string Name { get; } // Devs implement: public string Name => nameof(MyStep);
    Task ExecuteAsync(TData data, TEvent @event, CancellationToken ct = default);
    Task CompensateAsync(TData data, TEvent @event, CancellationToken ct = default);
}
```

---

### 5. [MEDIUM] `InMemoryOutboxStore.ClaimPendingAsync` — Race condition với concurrent workers

**File:** [`IOutboxStore.cs`](file:///e:/Workspace/My/LinKit/LinKit/LinKit.Outbox/IOutboxStore.cs#L91-L106)

```csharp
foreach (var m in _messages.Values.OrderBy(x => x.CreatedAtUtc))  // snapshot
{
    lock (m)  // ← lock PER MESSAGE nhưng snapshot đã lấy rồi
    { /* check & set LockedUntilUtc */ }
    claimed.Add(m);
}
```

**Race condition:** Thread A và Thread B cùng lấy snapshot → cùng thấy `m` chưa locked → cả hai add vào `claimed` → **duplicate delivery**.

**Fix:**
```csharp
private readonly object _claimLock = new();

public Task<IReadOnlyList<IOutboxMessage>> ClaimPendingAsync(int batchSize, TimeSpan lease, CancellationToken ct = default)
{
    var now = _time.GetUtcNow().UtcDateTime;
    lock (_claimLock)
    {
        var claimed = new List<IOutboxMessage>();
        foreach (var m in _messages.Values.OrderBy(x => x.CreatedAtUtc))
        {
            if (claimed.Count >= batchSize) break;
            if (!IsDue(m, now)) continue;
            m.LockedUntilUtc = now + lease;
            claimed.Add(m);
        }
        return Task.FromResult<IReadOnlyList<IOutboxMessage>>(claimed);
    }
}
```

---

### 6. [MEDIUM] `DataGenerator` — Không scan inherited properties từ base class

**File:** [`DataGenerator.cs`](file:///e:/Workspace/My/LinKit/LinKit/LinKit.Data.Generator/DataGenerator.cs#L200-L450)

```csharp
// Hiện tại chỉ scan direct members:
foreach (var member in classSymbol.GetMembers().OfType<IPropertySymbol>())
```

**Simulation Bug:**
```csharp
public class AuditableEntity
{
    public Guid Id { get; set; }           // ← BỊ BỎ QUA
    public DateTime CreatedAt { get; set; } // ← BỊ BỎ QUA
}

[Table("Products")]
public class ProductEntity : AuditableEntity
{
    public string Name { get; set; }        // ← CHỈ CÁI NÀY ĐƯỢC SCAN
}

// Generated SQL sai:
// INSERT INTO "Products" (Name) VALUES (@Name)   ← thiếu Id, CreatedAt
// SELECT "Name" FROM "Products" WHERE "Id" = @id ← Id không tồn tại trong SELECT
```

**Fix:**
```csharp
private static IEnumerable<IPropertySymbol> GetAllProperties(INamedTypeSymbol symbol)
{
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var current = (INamedTypeSymbol?)symbol;
    while (current != null && current.SpecialType != SpecialType.System_Object)
    {
        foreach (var prop in current.GetMembers().OfType<IPropertySymbol>())
            if (prop.DeclaredAccessibility == Accessibility.Public && !prop.IsStatic && seen.Add(prop.Name))
                yield return prop;
        current = current.BaseType;
    }
}
```

---

### 7. [MEDIUM] `ExecuteScalarSqlAsync<TScalar>` — `Convert.ChangeType` không AOT-safe

**File:** [`DataGenerator.cs`](file:///e:/Workspace/My/LinKit/LinKit/LinKit.Data.Generator/DataGenerator.cs#L1600)

```csharp
return (TScalar)Convert.ChangeType(res, typeof(TScalar));
```

**Vấn đề:** `Convert.ChangeType` dùng reflection để tìm `IConvertible` implementation. Với custom value types trong Native AOT, có thể bị trim.

**Fix — generate type switch:**
```csharp
if (typeof(TScalar) == typeof(int))     return (TScalar)(object)Convert.ToInt32(res);
if (typeof(TScalar) == typeof(long))    return (TScalar)(object)Convert.ToInt64(res);
if (typeof(TScalar) == typeof(string))  return (TScalar)(object)(res.ToString() ?? "");
if (typeof(TScalar) == typeof(Guid))    return (TScalar)(object)Guid.Parse(res.ToString()!);
if (typeof(TScalar) == typeof(bool))    return (TScalar)(object)Convert.ToBoolean(res);
if (typeof(TScalar) == typeof(decimal)) return (TScalar)(object)Convert.ToDecimal(res);
if (RuntimeFeature.IsDynamicCodeSupported)
    return (TScalar)Convert.ChangeType(res, typeof(TScalar));
throw new NotSupportedException($"TScalar '{typeof(TScalar).Name}' not supported in AOT.");
```

---

### 8. [MEDIUM] `IValidator<T>` — Chỉ hỗ trợ synchronous validation

**File:** [`Validation.cs`](file:///e:/Workspace/My/LinKit/LinKit/LinKit.Core/Validation/Validation.cs)

```csharp
public interface IValidator<in T>
{
    IEnumerable<ValidationError> Validate(T instance); // sync only
}
```

**Vấn đề:** Validation nghiệp vụ phổ biến cần async (kiểm tra duplicate, kiểm tra FK từ DB). Dev bị buộc dùng `.GetAwaiter().GetResult()` → potential deadlock.

**Fix:**
```csharp
// Thêm async variant song song:
public interface IAsyncValidator<in T>
{
    Task<IEnumerable<ValidationError>> ValidateAsync(T instance, CancellationToken ct = default);
}
// Generator phát hiện và tạo AsyncValidationBehavior khi có IAsyncValidator<TRequest>
```

---

### 9. [MEDIUM] Custom repository methods — `CancellationToken.None` hardcoded

**File:** [`DataGenerator.cs`](file:///e:/Workspace/My/LinKit/LinKit/LinKit.Data.Generator/DataGenerator.cs#L1627)

```csharp
// Generated code cho custom methods luôn dùng:
sb.AppendLine("        var (conn, tx, shouldDispose) = await GetConnectionAsync(CancellationToken.None);");
```

**Vấn đề:** Nếu dev khai báo custom method với `CancellationToken ct`, nó bị hoàn toàn ignore trong generated implementation. DB query không thể cancel.

**Fix trong generator:**
```csharp
var ctParam = method.Parameters.FirstOrDefault(p => p.TypeFullName.Contains("CancellationToken"));
string ctArg = ctParam != null ? ctParam.Name : "CancellationToken.None";
sb.AppendLine($"        var (conn, tx, shouldDispose) = await GetConnectionAsync({ctArg});");
// Tương tự cho reader.ReadAsync(), cmd.ExecuteReaderAsync()...
```

---

### 10. [MEDIUM] `FluentQuery` — Multiple `.Where()` calls có thể gây parameter name collision

**File:** `DataGenerator.cs` (generated `XxxFluentQuery` class)

```csharp
private readonly List<object?> _parameters = new List<object?>();

public IFluentQuery<TEntity> Where(string sqlCondition, object? parameters = null)
{
    _whereConditions.Add(sqlCondition);
    if (parameters != null) _parameters.Add(parameters);
    return this;
}
```

**Simulation Bug:**
```csharp
var results = await repo.Query()
    .Where("CategoryId = @id", new Dictionary<string,object?>{{"id", catId}})
    .Where("SupplierId = @id", new Dictionary<string,object?>{{"id", supId}}) // SAME KEY!
    .ToListAsync();
// → DbException: Parameter @id already defined / last one wins (behavior undefined)
```

**Fix:** Prefix params với where-clause index khi building SQL:
```csharp
// Internal: rename @id → @w0_id, @w1_id khi flattening
```

---

### 11. [LOW] `Result<T>` — `implicit operator` tạo `Result<Result<T>>` vô tình

**File:** [`Result.cs`](file:///e:/Workspace/My/LinKit/LinKit/LinKit.Core/Results/Result.cs#L134)

```csharp
public static implicit operator Result<T>(T value) => Success(value);
```

**Vấn đề:**
```csharp
// Handler trả Result<Result<OrderDto>> vô tình:
public async Task<Result<OrderDto>> HandleAsync(...)
{
    var innerResult = Result<OrderDto>.NotFound("...");
    return innerResult; // implicit operator: Result<OrderDto> → Result<Result<OrderDto>>?
    // → Không, nhưng nếu T = Result<OrderDto> thì có thể confused
}
```

Nên thêm `where T : notnull` hoặc detect `T : Result<>` và throw compile error.

---

### 12. [LOW] `TenantAccessor` + Outbox — TenantId không propagate vào BackgroundService

**File:** [`Tenancy.cs`](file:///e:/Workspace/My/LinKit/LinKit/LinKit.Tenancy/Tenancy.cs) + [`OutboxPublisherService.cs`](file:///e:/Workspace/My/LinKit/LinKit/LinKit.Outbox/OutboxPublisherService.cs)

**Vấn đề:** `AsyncLocal<T>` propagates context vào child tasks nhưng `OutboxPublisherService` chạy trong background thread pool riêng — TenantId từ HTTP request không có mặt khi publish.

**Fix:** Lưu TenantId vào outbox message headers khi enqueue:
```csharp
// Khi lưu outbox message:
var headers = new MessageHeaders();
headers["linkit-tenant-id"] = tenantAccessor.Context.TenantId;

// OutboxPublisherService trước khi publish:
if (headers.TryGetValue("linkit-tenant-id", out var tenantId))
    tenantAccessor.Context.TenantId = tenantId?.ToString();
```

---

### 13. [LOW] `DataGenerator` — Không `ReportDiagnostic` khi entity thiếu PK

**File:** [`DataGenerator.cs`](file:///e:/Workspace/My/LinKit/LinKit/LinKit.Data.Generator/DataGenerator.cs#L451)

```csharp
var pk = properties.FirstOrDefault(p => p.IsPrimaryKey) ?? properties.FirstOrDefault();
```

**Vấn đề:** Entity không có PK → silently dùng property đầu tiên → generated SQL sai mà không có warning/error nào.

**Fix:** `spc.ReportDiagnostic(Diagnostic.Create(NoPrimaryKeyDescriptor, entity.Location))`.

---

### 14. [LOW] Filename có trailing space

**File:** `LinKit.Core/BackgroundJobs/FileJobHistoryLogger .cs` ← trailing space trong tên

**Vấn đề:** Gây issue trên Linux filesystem (case-sensitive), một số git clients, và Docker builds.

**Fix:** Rename thành `FileJobHistoryLogger.cs`.

---

## ✅ Điểm mạnh — So sánh v1 vs v2

| Feature | v1 | v2 |
|---------|-----|-----|
| DataReader mapping | ⚠️ ordinal risk | ✅ `GetOrdinal` + cache per result set |
| AttachParameters | ❌ reflection | ✅ typed, clear AOT error |
| InsertRange/DeleteRange | ❌ N+1 | ✅ multi-row batch + WHERE IN |
| UpdateRange | ❌ N+1 | ✅ single conn + local tx |
| MapperGeneratorPart | ⚠️ CompilationProvider | ✅ `ForAttributeWithMetadataName` |
| ExpressionSqlTranslator | ❌ DynamicInvoke | ✅ AOT guard + meaningful error |
| UnitOfWork | ⚠️ race | ✅ `SemaphoreSlim(1,1)` |
| Result pattern | ❌ | ✅ `Result<T>` + `ResultStatus` enum |
| Validation | ❌ | ✅ `IValidator<T>` interface |
| SoftDelete | ❌ | ✅ `[SoftDelete]` + auto SQL (all dialects) |
| CompositeIndex | ❌ | ✅ `[CompositeIndex]` attribute |
| Resilience | ❌ chỉ attributes | ✅ `ResiliencePipeline` retry+CB+timeout |
| Outbox | ⚠️ basic | ✅ lease + dead-letter + retry backoff |

---

## 🔬 Simulation Test Cases

### ✅ Case 1: Multi-handler, cùng response type
```csharp
// Hai handlers, cùng trả Result<OrderDto>
public class CreateOrderHandler : ICommandHandler<CreateOrderCommand, Result<OrderDto>> { }
public class UpdateOrderHandler : ICommandHandler<UpdateOrderCommand, Result<OrderDto>> { }

// Generated switch dispatch:
(object)command switch {
    CreateOrderCommand c => (Task<TResponse>)(object)HandleResultRequest(c, ct),
    UpdateOrderCommand c => (Task<TResponse>)(object)HandleResultRequest(c, ct),
    _ => throw new HandlerNotFoundException(...)
};
// ✅ PASS: Dispatch đúng theo RequestType
```

### ❌ Case 2: Entity kế thừa (Bug #6)
```csharp
public class AuditableBase { public Guid Id { get; set; } public DateTime CreatedAt { get; set; } }
[Table("Products")] public class Product : AuditableBase { public string Name { get; set; } }

// Generated INSERT: (Name) VALUES (@Name)   ← THIẾU Id, CreatedAt
// Generated SELECT: "Name" FROM ...         ← THIẾU cột Id, CreatedAt
// ❌ FAIL: Schema sai, app crash hoặc data corrupt
```

### ✅ Case 3: Batch INSERT parameter limit
```csharp
// Entity 10 cột, cần insert 1000 items
// maxInsertBatch = Math.Min(500, 2000/10) = 200 items/batch
// 200 × 10 = 2000 params < SQL Server 2100 limit
// ✅ PASS: Batch size tự động đúng
```

### ✅ Case 4: Soft Delete + DeleteRangeAsync
```csharp
[SoftDelete("DeletedAt")] [Table("Orders")] public class Order { }
// DeleteBatchAsync → UPDATE SET DeletedAt = NOW() WHERE Id IN (...)
// ListAsync → WHERE DeletedAt IS NULL
// ✅ PASS: Soft delete consistent qua tất cả dialects
```

### ⚠️ Case 5: AOT + LINQ complex closure
```csharp
string name = "test";
// ✅ SAFE: local variable → MemberExpression → FieldInfo.GetValue (not reflection on type)
var r1 = await repo.AsQueryable().Where(p => p.Name == name).ToListAsync();

// ❌ KHÔNG SAFE (NativeAOT):
Func<string> getFilter = () => "test";
var r2 = await repo.AsQueryable().Where(p => p.Name == getFilter()).ToListAsync();
// → RuntimeFeature.IsDynamicCodeSupported = false → NotSupportedException (graceful)
```

### ⚠️ Case 6: Behavior ordering — counter-intuitive
```csharp
[CqrsBehavior(typeof(ICommand), order: -10)] class ValidationBehavior<TReq, TRes> { }
[CqrsBehavior(typeof(ICommand), order: 0)]   class LoggingBehavior<TReq, TRes> { }
[CqrsBehavior(typeof(ICommand), order: 10)]  class CacheBehavior<TReq, TRes> { }

// Execution order (outer → inner):
// CacheBehavior (10) → LoggingBehavior (0) → ValidationBehavior (-10) → Handler
// ⚠️ TINH TẾ: order thấp = chạy GẦN handler nhất
// Cần document rõ ràng trong README
```

### ❌ Case 7: Multi-tenant Outbox
```csharp
// Request handler (tenant-1):
tenantAccessor.Context.TenantId = "tenant-1";
await outboxStore.SaveMessageAsync(new OutboxMessage { MessageType = "OrderCreated", ... });
// Message được lưu nhưng TenantId KHÔNG được lưu vào headers

// OutboxPublisherService (background thread):
// TenantContext.TenantId = null ← lost!
// Producer publish message nhưng downstream consumer không biết tenant
// ❌ FAIL: Multi-tenant data leakage risk
```

### ⚠️ Case 8: Saga double failure
```csharp
await coordinator.ExecuteStepAsync(correlationId, orderEvent, step, "Processing");
// ExecuteAsync throws → CompensateAsync called
// CompensateAsync ALSO throws → LogCritical
// CurrentState vẫn là old state
// ⚠️ Saga stuck — cần manual intervention
// Fix: Lưu "Processing_CompensationFailed" state để có thể query và alert
```

---

## 🚀 Roadmap Tính năng

### Tier 1 — Critical for Production

#### A. `IAsyncValidator<T>` — Async validation
```csharp
public class CreateOrderValidator : IAsyncValidator<CreateOrderCommand>
{
    private readonly IProductRepository _repo;
    
    public async Task<IEnumerable<ValidationError>> ValidateAsync(
        CreateOrderCommand cmd, CancellationToken ct)
    {
        var errors = new List<ValidationError>();
        if (!await _repo.ExistsAsync(cmd.ProductId, ct))
            errors.Add(new ValidationError("ProductId", "Product not found"));
        return errors;
    }
}
// Generator auto-discovers + registers + wraps in AsyncValidationBehavior
```

#### B. Entity inheritance scan (Fix Bug #6)
Generator traverse `BaseType` chain để collect tất cả properties.

#### C. `Result<T>` → HTTP auto-mapping trong Endpoints
```csharp
// Generator detect nếu ResponseType là Result<T>:
var result = await mediator.QueryAsync<Result<ProductDto>>(query, ct);
return result.Status switch
{
    ResultStatus.Ok          => Results.Ok(result.Value),
    ResultStatus.NotFound    => Results.NotFound(result.ErrorMessage),
    ResultStatus.BadRequest  => Results.BadRequest(result.ValidationErrors),
    ResultStatus.Conflict    => Results.Conflict(result.ErrorMessage),
    ResultStatus.Unauthorized => Results.Unauthorized(),
    ResultStatus.Forbidden   => Results.Forbid(),
    _                        => Results.Problem(result.ErrorMessage)
};
```

#### D. Durable Saga Repository via DataGenerator
```csharp
[SagaEntity(typeof(OrderSagaData))]
public interface IOrderSagaRepository : ISagaRepository<OrderSagaData> { }
// Generator tạo: SQL schema + ISagaRepository<OrderSagaData> implementation
```

---

### Tier 2 — High Value

#### E. `[MultiTenant("TenantId")]` auto-filter
```csharp
[Table("Products")]
[MultiTenant("TenantId")]
public class ProductEntity
{
    public Guid TenantId { get; set; }
    // All queries auto-add: WHERE "TenantId" = @tenantId
    // Repository constructor inject ITenantAccessor
}
```

#### F. `[Cache]` behavior attribute
```csharp
[Cache(ttlSeconds: 300, varyBy: ["CategoryId"])]
public record GetProductsQuery(Guid CategoryId) : IQuery<List<ProductDto>>;
// Generator tạo CacheBehavior với IDistributedCache
// Cache key: "GetProductsQuery:CategoryId={CategoryId}"
```

#### G. `[Audit]` entity auto-tracking
```csharp
[Table("Orders")] [Audit]
public class OrderEntity
{
    public string CreatedBy { get; set; } = "";
    public string UpdatedBy { get; set; } = "";
    // InsertAsync auto-set: CreatedBy = currentUser.Id
    // UpdateAsync auto-set: UpdatedBy = currentUser.Id
}
// Repository inject ICurrentUserAccessor
```

#### H. Durable Outbox via DataGenerator
```csharp
[DataRepository]
public interface IMyOutboxRepository : IOutboxStore { }
// Generator tạo: OutboxMessages table + full IOutboxStore impl với SKIP LOCKED
```

---

### Tier 3 — Nice to Have

#### I. Health Check generator
```csharp
services.AddLinKitHealthChecks(); // auto-gen kiểm tra DB, messaging, saga
```

#### J. OpenAPI từ XML comments
```csharp
/// <summary>Get all products in a category</summary>
[HttpGet("products/{categoryId}")]
public record GetProductsQuery(Guid CategoryId) : IQuery<List<ProductDto>>;
// Generator → .WithSummary("Get all products in a category")
```

#### K. `[SoftDeleteFilter]` cho custom SQL methods
Custom methods hiện tại không auto-filter soft-deleted records.

---

## 📋 Best Practices Checklist

| Item | Status | Priority |
|------|--------|----------|
| No reflection hot path | ✅ | — |
| AOT-compatible (attributes) | ✅ | — |
| AOT-guarded dynamic paths | ✅ | — |
| Incremental gen perf (CQRS/Mapper) | ✅ | — |
| **Incremental gen perf (Endpoints)** | ⚠️ | HIGH |
| Batch operations (Insert/Delete) | ✅ | — |
| Thread safety (UoW) | ✅ | — |
| **InMemory outbox race condition** | ❌ | HIGH |
| **Entity inheritance support** | ❌ | HIGH |
| **Async validation** | ❌ | HIGH |
| **Result<T> HTTP auto-mapping** | ❌ | HIGH |
| **CancellationToken in custom methods** | ❌ | HIGH |
| `ExecuteScalarSqlAsync` AOT safety | ⚠️ | MEDIUM |
| Multi-tenant + Outbox propagation | ❌ | MEDIUM |
| Durable Saga persistence | ❌ | MEDIUM |
| MultiTenant auto-filter | ❌ | MEDIUM |
| Unit tests coverage | ❌ | HIGH |
| XML doc coverage | ⚠️ | LOW |
| Filename spaces | ⚠️ | LOW |
| Behavior ordering documentation | ⚠️ | LOW |

---

## 🎯 Thứ tự ưu tiên fix

### 🔴 Ngay (blocking/correctness):
1. **Bug #6** — Entity inherited properties — bất kỳ app nào dùng base class sẽ bị broken
2. **Bug #9** — CancellationToken ignored trong custom repo methods
3. **Bug #5** — InMemoryOutboxStore race condition → duplicate delivery trong tests

### 🟡 Sớm (performance/production):
4. **Bug #1/#2** — EndpointsGeneratorPart `CreateSyntaxProvider` + `CompilationProvider` rebuild perf
5. **Bug #7** — `ExecuteScalarSqlAsync` `Convert.ChangeType` AOT risk
6. **Feature C** — Result<T> HTTP auto-mapping → massive DX improvement
7. **Feature A** — Async validation → production necessity

### 🟢 Kế hoạch:
8. **Feature E** — MultiTenant auto-filter
9. **Feature D** — Durable Saga via DataGenerator
10. **Feature H** — Durable Outbox via DataGenerator
