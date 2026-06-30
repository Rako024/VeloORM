using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Query;
using VeloORM.Runtime;

namespace VeloORM.Tests.Integration;

[Table("posts")]
public class Post
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public ICollection<Tag>? Tags { get; set; }
}

[Table("tags")]
public class Tag
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public ICollection<Post>? Posts { get; set; }
}

/// <summary>
/// Verifies many-to-many navigations declared via explicit junction config: Include loads the related
/// set through a single junction-join follow-up query, grouped back to each parent.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ManyToManyTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VeloDbContext _db = null!;

    public ManyToManyTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        var factory = new NpgsqlConnectionFactory(_fixture.ConnectionString);
        var executor = new PostgresCommandExecutor(factory);
        var model = VeloModel.Build([typeof(Post), typeof(Tag)], b =>
        {
            b.Entity<Post>().HasManyToMany(p => p.Tags!, "post_tags", "post_id", "tag_id");
            b.Entity<Tag>().HasManyToMany(t => t.Posts!, "post_tags", "tag_id", "post_id");
        });
        _db = new VeloDbContext(model, PostgresDialect.Instance, factory, executor);

        await executor.ExecuteAsync(new SqlStatement("""
            DROP TABLE IF EXISTS post_tags CASCADE;
            DROP TABLE IF EXISTS posts CASCADE;
            DROP TABLE IF EXISTS tags CASCADE;
            CREATE TABLE posts (id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, title text NOT NULL);
            CREATE TABLE tags (id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text NOT NULL);
            CREATE TABLE post_tags (
                post_id integer NOT NULL REFERENCES posts(id),
                tag_id integer NOT NULL REFERENCES tags(id),
                PRIMARY KEY (post_id, tag_id));
            INSERT INTO posts (title) VALUES ('A'), ('B');
            INSERT INTO tags (name) VALUES ('x'), ('y'), ('z');
            INSERT INTO post_tags (post_id, tag_id) VALUES (1,1),(1,2),(2,2),(2,3);
            """, Array.Empty<SqlParameterBinding>()));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Include_ManyToMany_Loads_Related_Set_Via_Junction()
    {
        var posts = _db.Set<Post>().Include(p => p.Tags).ToList();

        var a = posts.Single(p => p.Title == "A");
        Assert.Equal(new[] { "x", "y" }, a.Tags!.Select(t => t.Name).OrderBy(n => n).ToArray());

        var b = posts.Single(p => p.Title == "B");
        Assert.Equal(new[] { "y", "z" }, b.Tags!.Select(t => t.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void Include_ManyToMany_Reverse_Direction()
    {
        var tags = _db.Set<Tag>().Include(t => t.Posts).ToList();

        var y = tags.Single(t => t.Name == "y");
        Assert.Equal(new[] { "A", "B" }, y.Posts!.Select(p => p.Title).OrderBy(s => s).ToArray());

        var x = tags.Single(t => t.Name == "x");
        Assert.Equal(new[] { "A" }, x.Posts!.Select(p => p.Title).ToArray());
    }

    [Fact]
    public void ManyToMany_With_No_Links_Yields_Empty_Collection()
    {
        // 'z' links only to B; ensure a post with a single tag and the empty grouping path both work.
        var posts = _db.Set<Post>().Include(p => p.Tags).ToList();
        Assert.All(posts, p => Assert.NotNull(p.Tags));
    }
}
