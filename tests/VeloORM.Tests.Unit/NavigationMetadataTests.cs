using VeloORM.Metadata;

namespace VeloORM.Tests.Unit;

public class NavUser
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public ICollection<NavOrder>? Orders { get; set; }
}

public class NavOrder
{
    public int Id { get; set; }
    public decimal Total { get; set; }
    public int UserId { get; set; }
    public NavUser? User { get; set; }
}

// Uses a [ForeignKey] override and a non-conventional FK name.
public class FkOrder
{
    public int Id { get; set; }
    public int BuyerId { get; set; }

    [ForeignKey(nameof(BuyerId))]
    public NavUser? Buyer { get; set; }
}

public class NavigationMetadataTests
{
    [Fact]
    public void Reference_Navigation_Is_Resolved_By_Convention()
    {
        var order = VeloModel.Build([typeof(NavUser), typeof(NavOrder)]).GetEntity<NavOrder>();

        var nav = order.FindNavigation("User");
        Assert.NotNull(nav);
        Assert.Equal(NavigationKind.Reference, nav!.Kind);
        Assert.Equal(typeof(NavUser), nav.TargetClrType);
        Assert.Equal("user_id", nav.LocalKeyColumnName);   // FK on Order
        Assert.Equal("id", nav.TargetKeyColumnName);       // PK on User
    }

    [Fact]
    public void Collection_Navigation_Is_Resolved_By_Convention()
    {
        var user = VeloModel.Build([typeof(NavUser), typeof(NavOrder)]).GetEntity<NavUser>();

        var nav = user.FindNavigation("Orders");
        Assert.NotNull(nav);
        Assert.Equal(NavigationKind.Collection, nav!.Kind);
        Assert.Equal(typeof(NavOrder), nav.TargetClrType);
        Assert.Equal("id", nav.LocalKeyColumnName);        // PK on User
        Assert.Equal("user_id", nav.TargetKeyColumnName);  // FK on Order
    }

    [Fact]
    public void ForeignKey_Attribute_Overrides_Convention()
    {
        var order = VeloModel.Build([typeof(NavUser), typeof(FkOrder)]).GetEntity<FkOrder>();

        var nav = order.FindNavigation("Buyer");
        Assert.NotNull(nav);
        Assert.Equal("buyer_id", nav!.LocalKeyColumnName);
    }

    [Fact]
    public void Navigation_To_Type_Outside_Model_Is_Skipped()
    {
        // NavUser is not part of the model -> Order.User is not navigable.
        var order = VeloModel.Build([typeof(NavOrder)]).GetEntity<NavOrder>();
        Assert.Null(order.FindNavigation("User"));
        // The scalar FK column is still mapped.
        Assert.NotNull(order.FindColumnByProperty("UserId"));
    }
}
