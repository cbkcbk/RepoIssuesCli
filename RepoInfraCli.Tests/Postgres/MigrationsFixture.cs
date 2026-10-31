namespace RepoInfraCli.Tests.Postgres;

public static class MigrationsFixture
{
    public const string MigrationId = "20260101000000_Widgets";

    const string Project = """
                           <Project Sdk="Microsoft.NET.Sdk">
                             <PropertyGroup>
                               <OutputType>Exe</OutputType>
                               <TargetFramework>net10.0</TargetFramework>
                               <ImplicitUsings>enable</ImplicitUsings>
                               <Nullable>enable</Nullable>
                             </PropertyGroup>
                             <ItemGroup>
                               <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
                               <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.14" PrivateAssets="all" />
                             </ItemGroup>
                           </Project>
                           """;

    const string Code = $$"""
                          using Microsoft.EntityFrameworkCore;
                          using Microsoft.EntityFrameworkCore.Design;
                          using Microsoft.EntityFrameworkCore.Diagnostics;
                          using Microsoft.EntityFrameworkCore.Infrastructure;
                          using Microsoft.EntityFrameworkCore.Migrations;

                          return;

                          public sealed class Widget
                          {
                              public int Id { get; set; }

                              public string Name { get; set; } = "";
                          }

                          public sealed class WidgetsContext(DbContextOptions<WidgetsContext> options) : DbContext(options)
                          {
                              public DbSet<Widget> Widgets => Set<Widget>();

                              protected override void OnModelCreating(ModelBuilder model) =>
                                  model.Entity<Widget>(widget =>
                                  {
                                      widget.ToTable("widgets");
                                      widget.Property(item => item.Id).HasColumnName("id");
                                      widget.Property(item => item.Name).HasColumnName("name");
                                  });
                          }

                          public sealed class WidgetsFactory : IDesignTimeDbContextFactory<WidgetsContext>
                          {
                              public WidgetsContext CreateDbContext(string[] args) =>
                                  new(new DbContextOptionsBuilder<WidgetsContext>()
                                      .UseNpgsql("Host=unused")
                                      .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                                      .Options);
                          }

                          [DbContext(typeof(WidgetsContext))]
                          [Migration("{{MigrationId}}")]
                          public sealed class Widgets : Migration
                          {
                              protected override void Up(MigrationBuilder migration) =>
                                  migration.CreateTable(
                                      "widgets",
                                      table => new { id = table.Column<int>(nullable: false), name = table.Column<string>(nullable: false) },
                                      constraints: table => table.PrimaryKey("pk_widgets", row => row.id));

                              protected override void Down(MigrationBuilder migration) => migration.DropTable("widgets");
                          }
                          """;

    public static string Write(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Program.cs"), Code);
        var project = Path.Combine(folder, "Widgets.csproj");
        File.WriteAllText(project, Project);
        return project;
    }
}
