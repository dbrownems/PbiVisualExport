using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiVisualExport.Tests;

public class FieldParametersTests
{
    [Fact]
    public void FieldParamTest_Fields_AreTheColumnsInTheParameter()
    {
        Assert.Equal(new[]
        {
            new VisualFieldRef(VisualFieldKind.Column, "Product", "Category"),
            new VisualFieldRef(VisualFieldKind.Column, "Product", "Segment"),
            new VisualFieldRef(VisualFieldKind.Column, "Product", "Product")
        }, FieldParameters.Fields(Fixtures.Model("FieldParamTest"), "fields"));
    }

    [Theory]
    [InlineData("Product")] // not a field parameter
    [InlineData("Missing")]
    public void OtherTables_HaveNoFields(string table) =>
        Assert.Empty(FieldParameters.Fields(Fixtures.Model("FieldParamTest"), table));

    [Fact]
    public void NameOfReferences_SkipStringLiteralsAndComments()
    {
        var dax = """
            {
                ("Category", NAMEOF('Product'[Category]), 0),
                ("A ""NAMEOF('Hidden'[Label])"" label", nameof ( Sales [Amount] ), 1),
                // ("Old", NAMEOF('HR'[Salary]), 2),
                -- ("Older", NAMEOF('HR'[Bonus]), 3),
                /* ("Oldest", NAMEOF('HR'[Grade]), 4), */
                ("Total", NAMEOF([Total]), 5),
                ("Odd", NAMEOF('O''Brien -- not a comment'[a]]b "c"]), 6)
            }
            """;
        Assert.Equal(new (string?, string)[]
        {
            ("Product", "Category"), ("Sales", "Amount"), (null, "Total"), ("O'Brien -- not a comment", "a]b \"c\"")
        }, FieldParameters.NameOfReferences(dax));
    }

    [Fact]
    public void References_ResolveToColumnsAndMeasures_AndUnknownOnesAreSkipped()
    {
        var model = Samples.Model();
        AddFieldParameter(model, "Params", """
            {
                ("Region", NAMEOF('Sales'[Region]), 0),
                ("Total", NAMEOF('Sales'[Total]), 1),
                ("Count", NAMEOF([count]), 2),
                ("Odd", NAMEOF('O''Brien'[a]]b]), 3),
                ("Dropped", NAMEOF('Sales'[Dropped]), 4),
                ("Wrong table", NAMEOF('O''Brien'[Total]), 5),
                ("Again", NAMEOF(Sales[REGION]), 6)
            }
            """);
        Assert.Equal(new[]
        {
            new VisualFieldRef(VisualFieldKind.Column, "Sales", "Region"),
            new VisualFieldRef(VisualFieldKind.Measure, "Sales", "Total"),
            new VisualFieldRef(VisualFieldKind.Measure, "Sales", "Count"),
            new VisualFieldRef(VisualFieldKind.Column, "O'Brien", "a]b")
        }, FieldParameters.Fields(model, "params"));
    }

    [Fact]
    public void ACalculatedTableWithoutTheFieldParameterKind_HasNoFields()
    {
        var model = Samples.Model();
        AddFieldParameter(model, "Params", """{ ("Region", NAMEOF('Sales'[Region]), 0) }""", kind: 1);
        Assert.Empty(FieldParameters.Fields(model, "Params"));
    }

    /// <summary>A field parameter table as Power BI Desktop shapes it: label, hidden field reference (with ParameterMetadata), order.</summary>
    private static void AddFieldParameter(TOM.Model model, string name, string expression, int kind = 2)
    {
        var table = new TOM.Table { Name = name };
        table.Columns.Add(new TOM.CalculatedTableColumn { Name = name, SourceColumn = "[Value1]", DataType = TOM.DataType.String });
        var fields = new TOM.CalculatedTableColumn { Name = name + " Fields", SourceColumn = "[Value2]", DataType = TOM.DataType.String, IsHidden = true };
        fields.ExtendedProperties.Add(new TOM.JsonExtendedProperty { Name = "ParameterMetadata", Value = $$"""{"version":3,"kind":{{kind}}}""" });
        table.Columns.Add(fields);
        table.Columns.Add(new TOM.CalculatedTableColumn { Name = name + " Order", SourceColumn = "[Value3]", DataType = TOM.DataType.Int64, IsHidden = true });
        table.Partitions.Add(new TOM.Partition { Name = name, Source = new TOM.CalculatedPartitionSource { Expression = expression } });
        model.Tables.Add(table);
    }
}
