using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace VeloORM.Metadata;

/// <summary>
/// The resolved metadata for a whole context: a thread-safe registry of
/// <see cref="EntityModel"/>s keyed by CLR type. Built once and reused.
/// </summary>
public sealed class VeloModel
{
    private readonly ConcurrentDictionary<Type, EntityModel> _entities;

    private VeloModel(IEnumerable<EntityModel> entities) =>
        _entities = new ConcurrentDictionary<Type, EntityModel>(
            entities.ToDictionary(e => e.ClrType));

    public IReadOnlyCollection<EntityModel> Entities => (IReadOnlyCollection<EntityModel>)_entities.Values;

    public EntityModel? FindEntity(Type clrType) =>
        _entities.TryGetValue(clrType, out var m) ? m : null;

    public EntityModel GetEntity(Type clrType) =>
        FindEntity(clrType)
        ?? throw new InvalidOperationException($"Type '{clrType.Name}' is not part of the model.");

    public EntityModel GetEntity<T>() => GetEntity(typeof(T));

    /// <summary>Builds a model from the given entity types, applying a fluent <see cref="ModelBuilder"/>.
    /// This is the reflection-based path used when no source-generated model is available; the
    /// generated path (AOT/trim-friendly) supplies its model without reflection.</summary>
    [RequiresUnreferencedCode("Runtime model building reflects over entity properties. " +
        "For trimming/AOT, use the source-generated model.")]
    public static VeloModel Build(
        IEnumerable<Type> entityTypes,
        Action<ModelBuilder>? configure = null,
        INamingConvention? naming = null)
    {
        var modelBuilder = new ModelBuilder();
        configure?.Invoke(modelBuilder);

        var factory = new EntityModelFactory(naming);
        var models = new List<EntityModel>();

        // Include explicitly-configured types even if not in the supplied set.
        var allTypes = new HashSet<Type>(entityTypes);
        foreach (var cfg in modelBuilder.Configurations)
            allTypes.Add(cfg.ClrType);

        foreach (var type in allTypes)
            models.Add(factory.Create(type, modelBuilder.FindConfiguration(type)));

        return new VeloModel(models);
    }
}
