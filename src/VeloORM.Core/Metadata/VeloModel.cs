using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace VeloORM.Metadata;

/// <summary>Model-wide build options.</summary>
public sealed class VeloModelOptions
{
    /// <summary>When true, every mapped <see cref="DateTime"/> column is treated as UTC — read values
    /// are stamped <see cref="DateTimeKind.Utc"/>, as if each property carried
    /// <see cref="UtcDateTimeAttribute"/>. Writes of any <see cref="DateTime"/> are stored as
    /// <see cref="DateTimeKind.Unspecified"/> regardless (matching the <c>timestamp</c> column type).</summary>
    public bool NormalizeDateTimesToUtc { get; set; }
}

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
        INamingConvention? naming = null,
        VeloModelOptions? options = null)
    {
        var modelBuilder = new ModelBuilder();
        configure?.Invoke(modelBuilder);

        var factory = new EntityModelFactory(naming, options);
        var models = new List<EntityModel>();

        // Include explicitly-configured types even if not in the supplied set.
        var allTypes = new HashSet<Type>(entityTypes);
        foreach (var cfg in modelBuilder.Configurations)
            allTypes.Add(cfg.ClrType);

        foreach (var type in allTypes)
            models.Add(factory.Create(type, modelBuilder.FindConfiguration(type)));

        // Second pass: resolve navigations now that every entity's columns/keys are known.
        NavigationResolver.Resolve(models);

        // Third pass: explicit many-to-many navigations (require a junction table, declared via fluent).
        ApplyManyToMany(models, modelBuilder);

        return new VeloModel(models);
    }

    [RequiresUnreferencedCode("Reflects over entity properties to bind many-to-many navigations.")]
    private static void ApplyManyToMany(List<EntityModel> models, ModelBuilder modelBuilder)
    {
        var byType = models.ToDictionary(m => m.ClrType);
        foreach (var cfg in modelBuilder.Configurations)
        {
            if (cfg.ManyToMany.Count == 0)
                continue;
            if (!byType.TryGetValue(cfg.ClrType, out var entity) || entity.KeyColumns.Count != 1)
                continue;

            var added = new List<NavigationModel>(entity.Navigations);
            foreach (var m2m in cfg.ManyToMany)
            {
                if (!byType.TryGetValue(m2m.TargetType, out var target) || target.KeyColumns.Count != 1)
                    continue;
                var property = cfg.ClrType.GetProperty(m2m.NavigationProperty);
                if (property is null)
                    continue;

                added.Add(NavigationModel.ManyToManyNav(
                    property, m2m.TargetType,
                    localKeyColumnName: entity.KeyColumns[0].ColumnName,
                    targetKeyColumnName: target.KeyColumns[0].ColumnName,
                    m2m.JunctionSchema, m2m.JunctionTable, m2m.LocalKeyColumn, m2m.TargetKeyColumn));
            }
            entity.Navigations = added;
        }
    }
}
