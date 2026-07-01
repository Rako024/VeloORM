using VeloORM.Metadata;

namespace VeloORM.Tests.Unit;

public sealed class DateTimeNormalizationTests
{
    private class Event
    {
        public int Id { get; set; }
        public DateTime CreatedAt { get; set; }
        [UtcDateTime] public DateTime UpdatedAt { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
    }

    private static ColumnModel Column(VeloModel model, string property) =>
        model.GetEntity(typeof(Event)).Columns.First(c => c.PropertyName == property);

    [Fact]
    public void Attribute_marks_only_that_column()
    {
        var model = VeloModel.Build([typeof(Event)]);
        Assert.False(Column(model, nameof(Event.CreatedAt)).NormalizeToUtc);
        Assert.True(Column(model, nameof(Event.UpdatedAt)).NormalizeToUtc);
    }

    [Fact]
    public void Global_option_marks_all_datetime_columns()
    {
        var model = VeloModel.Build([typeof(Event)], options: new VeloModelOptions { NormalizeDateTimesToUtc = true });
        Assert.True(Column(model, nameof(Event.CreatedAt)).NormalizeToUtc);
        Assert.True(Column(model, nameof(Event.UpdatedAt)).NormalizeToUtc);
        // DateTimeOffset is time-zone-aware already; the flag is DateTime-only.
        Assert.False(Column(model, nameof(Event.OccurredAt)).NormalizeToUtc);
    }

    [Fact]
    public void Fluent_AsUtc_marks_the_column()
    {
        var model = VeloModel.Build([typeof(Event)],
            configure: b => b.Entity<Event>().Property(e => e.CreatedAt).AsUtc());
        Assert.True(Column(model, nameof(Event.CreatedAt)).NormalizeToUtc);
    }
}
