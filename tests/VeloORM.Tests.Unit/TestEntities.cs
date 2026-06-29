using VeloORM.Metadata;

namespace VeloORM.Tests.Unit;

// Convention-only entity: "Id" -> key + serial; PascalCase -> snake_case; pluralized table.
public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Email { get; set; }       // nullable reference -> nullable column
    public int LoginCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// Attribute-driven entity.
[Table("people", Schema = "app")]
[Index(nameof(NationalId), IsUnique = true)]
public class Person
{
    [Key]
    public Guid PersonKey { get; set; }

    [Column("full_name", TypeName = "varchar(200)")]
    public string FullName { get; set; } = "";

    public string NationalId { get; set; } = "";

    [NotMapped]
    public string ComputedDisplay => FullName;

    public ICollection<User>? Friends { get; set; }  // navigation -> not mapped (non-scalar)
}

// Composite-key entity configured via fluent API in tests.
public class OrderLine
{
    public int OrderId { get; set; }
    public int LineNumber { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
}
