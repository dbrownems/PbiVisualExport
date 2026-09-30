namespace PbiVisualExport.Tests;

public class DaxQueryBuilderTests
{
    private static readonly Lazy<Microsoft.AnalysisServices.Tabular.Model> SampleModel = new(Samples.Model);
    private static Microsoft.AnalysisServices.Tabular.Model Model => SampleModel.Value;

    private static DaxPlan Build(VisualExportRequest r, ReportDefinition? report = null) => new DaxQueryBuilder().Build(r, Model, report);

    private static string Basic(string column, string op, string values) =>
        $$"""{"$schema":"http://powerbi.com/product/schema#basic","target":{"table":"Sales","column":"{{column}}"},"filterType":1,"operator":"{{op}}","values":{{values}}}""";

    private static string Advanced(string target, string logical, string conditions) =>
        $$"""{"$schema":"http://powerbi.com/product/schema#advanced","target":{{target}},"filterType":0,"logicalOperator":"{{logical}}","conditions":{{conditions}}}""";

    // ---- FindUnresolvedFields ----

    [Fact]
    public void FindUnresolvedFields_ListsMissingColumnsMeasuresSortAndFilterMeasures()
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"), Samples.Column("Sales", "Nope"), Samples.Measure("Sales", "Total"),
            Samples.Measure("Sales", "Report Only"));
        r.Sort.Add(new ExportSort { Table = "Sales", Measure = "Sort Only" });
        r.Sort.Add(new ExportSort { Table = "Sales", Measure = "Report Only" });
        r.Filters.Add(Fixtures.Filter(Advanced("""{"table":"Sales","measure":"Filter Only"}""", "And", """[{"operator":"GreaterThan","value":1}]""")));
        r.Filters.Add(Fixtures.Filter(Advanced("""{"table":"Sales","measure":"Report Only"}""", "And", """[{"operator":"GreaterThan","value":1}]""")));
        r.Filters.Add(Fixtures.Filter(Advanced("""{"table":"Sales","measure":"Unset Filter"}""", "And", "[]")));
        r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#topN","target":{"table":"Sales","column":"Region"},"filterType":5,"operator":"Top","itemCount":2,"orderBy":{"table":"Sales","measure":"Rank By"}}"""));

        var u = DaxQueryBuilder.FindUnresolvedFields(r, Model);
        Assert.Equal(new[]
        {
            new UnresolvedField("'Sales'[Nope]", null, false),
            new UnresolvedField("[Report Only]", "Report Only", false),
            new UnresolvedField("[Sort Only]", "Sort Only", true),
            new UnresolvedField("[Report Only]", "Report Only", true),
            new UnresolvedField("[Filter Only]", "Filter Only", false),
            new UnresolvedField("[Rank By]", "Rank By", false)
        }, u);
    }

    [Fact]
    public void FindUnresolvedFields_IgnoresSortWhenSortIsOff_AndMatchesCaseInsensitively()
    {
        var r = Samples.Request(Samples.Column("sales", "region"), Samples.Measure("SALES", "total"));
        r.ApplySort = false;
        r.Sort.Add(new ExportSort { Table = "Sales", Measure = "Sort Only" });
        Assert.Empty(DaxQueryBuilder.FindUnresolvedFields(r, Model));
    }

    [Fact]
    public void FindUnresolvedFields_ListsColumnsOfActiveFilters()
    {
        // e.g. a slicer on a column added since the model was cached: refreshing may find it (and its parameter binding).
        var r = Samples.Request(Samples.Column("Sales", "Region"));
        r.Filters.Add(Fixtures.Filter(Basic("New Column", "In", """["x"]""")));
        r.Filters.Add(Fixtures.Filter(Basic("new column", "In", """["y"]""")));
        r.Filters.Add(Fixtures.Filter(Advanced("""{"table":"Sales","column":"New Advanced"}""", "And", """[{"operator":"Is","value":"x"}]""")));
        r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#topN","target":{"table":"Sales","column":"New TopN"},"filterType":5,"operator":"Top","itemCount":2,"orderBy":{"table":"Sales","measure":"Total"}}"""));
        // Inactive filters (nothing selected) and known columns don't count.
        r.Filters.Add(Fixtures.Filter(Basic("Unset", "In", "[]")));
        r.Filters.Add(Fixtures.Filter(Basic("All Selected", "All", """["x"]""")));
        r.Filters.Add(Fixtures.Filter(Advanced("""{"table":"Sales","column":"No Conditions"}""", "And", "[]")));
        r.Filters.Add(Fixtures.Filter(Basic("region", "In", """["West"]""")));

        Assert.Equal(new[]
        {
            new UnresolvedField("'Sales'[New Column]", null, false),
            new UnresolvedField("'Sales'[New Advanced]", null, false),
            new UnresolvedField("'Sales'[New TopN]", null, false)
        }, DaxQueryBuilder.FindUnresolvedFields(r, Model));
    }

    // ---- Shape of the query ----

    [Fact]
    public void Columns_AreGroupedAndFormatted_MeasuresFormatted_InVisualOrder()
    {
        var plan = Build(Samples.Request(Samples.Measure("Sales", "Total"), Samples.Column("Sales", "Region"), Samples.Column("Sales", "Amount"),
            Samples.Agg("Sales", "Amount", "Average")));
        Assert.Equal(new[] { "c0=Total", "c1=Region", "c2=Amount", "c3=Average of Amount" }, plan.Columns.Select(c => $"{c.Alias}={c.Header}"));
        Assert.Equal(new[] { true, false, false, true }, plan.Columns.Select(c => c.IsMeasure));
        var q = plan.BuildQuery();
        Assert.Contains("SUMMARIZECOLUMNS('Sales'[Region], 'Sales'[Amount], \"m_0\", [Total], \"m_1\", AVERAGE('Sales'[Amount]))", q);
        Assert.Contains("\"c0\", FORMAT([m_0], \"#,0\"), \"c1\", 'Sales'[Region], \"c2\", FORMAT('Sales'[Amount], \"#,0.00\"), \"c3\", FORMAT([m_1], \"#,0.00\")", q);
        // Implicit order: group-by columns ascending; the formatted Amount sorts on a raw key.
        Assert.EndsWith("ORDER BY [c1] ASC, [s0] ASC", q);
        Assert.Equal("Region ASC, Amount ASC (implicit default)", plan.SortDescription);
    }

    [Fact]
    public void RawValuesAndNoSort_WhenFormatAndSortAreOff()
    {
        var r = Samples.Request(Samples.Column("Sales", "Amount"), Samples.Measure("Sales", "Total"));
        r.ApplyFormatStrings = false;
        r.ApplySort = false;
        var plan = Build(r);
        var q = plan.BuildQuery();
        Assert.DoesNotContain("FORMAT(", q);
        Assert.DoesNotContain("ORDER BY", q);
        Assert.Equal("off", plan.SortDescription);
        Assert.False(plan.ApplyFormatStrings);
    }

    [Fact]
    public void MaxRows_WrapsInTopNWithTheSortOrder()
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"), Samples.Measure("Sales", "Total"));
        r.Sort.Add(new ExportSort { Table = "Sales", Measure = "Total", Direction = "Descending" });
        var q = Build(r).BuildQuery(maxRows: 10);
        Assert.Contains("TOPN(10, SELECTCOLUMNS(_summ, \"c0\", 'Sales'[Region], \"c1\", FORMAT([m_0], \"#,0\"), \"s0\", [m_0]), [s0], 0)", q);
        Assert.EndsWith("ORDER BY [s0] DESC", q);

        r.ApplySort = false;
        Assert.Contains("TOPN(10, SELECTCOLUMNS(", Build(r).BuildQuery(maxRows: 10));
    }

    [Fact]
    public void HiddenFields_AreComputedButNotOutput()
    {
        var hiddenMeasure = Samples.Measure("Sales", "Count");
        hiddenMeasure.Hidden = true;
        var hiddenColumn = Samples.Column("Sales", "Region");
        hiddenColumn.Hidden = true;
        var plan = Build(Samples.Request(hiddenColumn, Samples.Column("Sales", "Is Online"), Samples.Measure("Sales", "Total"), hiddenMeasure));

        Assert.Equal(new[] { "Is Online", "Total" }, plan.Columns.Select(c => c.Header));
        var q = plan.BuildQuery();
        Assert.Contains("SUMMARIZECOLUMNS('Sales'[Region], 'Sales'[Is Online], \"m_0\", [Total], \"m_1\", [Count])", q);
        Assert.DoesNotContain("\"c2\"", q);
    }

    [Fact]
    public void AllFieldsHidden_IsRejected()
    {
        var m = Samples.Measure("Sales", "Total");
        m.Hidden = true;
        var ex = Assert.Throws<ExportRequestException>(() => Build(Samples.Request(m)));
        Assert.Contains("no exportable columns or measures", ex.Message);
    }

    [Fact]
    public void Identifiers_AreEscaped()
    {
        var plan = Build(Samples.Request(Samples.Column("O'Brien", "a]b"), Samples.Measure("O'Brien", "m]x")));
        var q = plan.BuildQuery();
        Assert.Contains("SUMMARIZECOLUMNS('O''Brien'[a]]b], \"m_0\", [m]]x])", q);
        Assert.Equal(new[] { "a]b", "m]x" }, plan.Columns.Select(c => c.Header));
    }

    [Fact]
    public void HeaderOverride_IsUsed()
    {
        var f = Samples.Column("Sales", "Region");
        f.Header = "Sales Region";
        Assert.Equal("Sales Region", Build(Samples.Request(f)).Columns[0].Header);
    }

    // ---- Sort ----

    [Fact]
    public void SortByColumn_IsUsedAndGroupedOn()
    {
        var r = Samples.Request(Samples.Column("Sales", "Month Name"), Samples.Measure("Sales", "Total"));
        r.Sort.Add(new ExportSort { Table = "Sales", Column = "Month Name", Direction = "Descending" });
        var plan = Build(r);
        var q = plan.BuildQuery();
        Assert.Contains("SUMMARIZECOLUMNS('Sales'[Month Name], 'Sales'[Month Number], \"m_0\", [Total])", q);
        Assert.Contains("\"s0\", 'Sales'[Month Number]", q);
        Assert.EndsWith("ORDER BY [s0] DESC", q);
        Assert.Equal("Month Name DESC (visual sort)", plan.SortDescription);
    }

    [Fact]
    public void SortOnlyMeasureAndAggregation_AreComputedForTheSort()
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"));
        r.Sort.Add(new ExportSort { Table = "Sales", Measure = "Count", Direction = "Descending" });
        r.Sort.Add(new ExportSort { Table = "Sales", Column = "Amount", Aggregation = "Max" });
        var plan = Build(r);
        var q = plan.BuildQuery();
        Assert.Contains("\"m_0\", [Count], \"m_1\", MAX('Sales'[Amount])", q);
        Assert.EndsWith("ORDER BY [s0] DESC, [s1] ASC", q);
        Assert.Single(plan.Columns);
    }

    [Fact]
    public void SortOnAColumnNotInTheVisual_IsIgnoredWithAWarning()
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"));
        r.Sort.Add(new ExportSort { Table = "Sales", Column = "Amount" });
        r.Sort.Add(new ExportSort { Table = "Sales", Measure = "Missing" });
        var plan = Build(r);
        Assert.Equal(new[]
        {
            "Sort column 'Sales'[Amount] is not a visible column of the visual; ignored.",
            "Sort measure [Missing] not found in the model or the report's measures; ignored."
        }, plan.Warnings);
        Assert.Equal("none", plan.SortDescription);
        Assert.DoesNotContain("ORDER BY", plan.BuildQuery());
    }

    // ---- Filters ----

    [Fact]
    public void ColumnFilters_AreTranslatedByDataType()
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"));
        r.Filters.Add(Fixtures.Filter(Basic("Region", "In", """["It's", "B"]""")));
        r.Filters.Add(Fixtures.Filter(Basic("Is Online", "In", "[true]")));
        r.Filters.Add(Fixtures.Filter(Basic("Amount", "NotIn", """[1.5, "2", null]""")));
        r.Filters.Add(Fixtures.Filter(Advanced("""{"table":"Sales","column":"Order Date"}""", "And",
            """[{"operator":"GreaterThanOrEqual","value":"2024-01-02T03:04:05Z"},{"operator":"IsNot","value":"2024-06-01"}]""")));
        r.Filters.Add(Fixtures.Filter(Advanced("""{"table":"Sales","column":"Region"}""", "Or",
            """[{"operator":"StartsWith","value":"N"},{"operator":"DoesNotContain","value":"x"},{"operator":"IsNotBlank"}]""")));
        var q = Build(r).BuildQuery();

        Assert.Contains("VAR _f0 = TREATAS({\"It's\", \"B\"}, 'Sales'[Region])", q);
        Assert.Contains("VAR _f1 = TREATAS({TRUE()}, 'Sales'[Is Online])", q);
        Assert.Contains("VAR _f2 = FILTER(ALL('Sales'[Amount]), NOT('Sales'[Amount] IN {1.5, 2, BLANK()}))", q);
        Assert.Contains("VAR _f3 = FILTER(ALL('Sales'[Order Date]), 'Sales'[Order Date] >= (DATE(2024, 1, 2) + TIME(3, 4, 5)) && 'Sales'[Order Date] <> DATE(2024, 6, 1))", q);
        Assert.Contains("VAR _f4 = FILTER(ALL('Sales'[Region]), LEFT('Sales'[Region], LEN(\"N\")) = \"N\" || SEARCH(\"x\", 'Sales'[Region], 1, 0) = 0 || NOT(ISBLANK('Sales'[Region])))", q);
        Assert.Contains("SUMMARIZECOLUMNS('Sales'[Region], _f0, _f1, _f2, _f3, _f4)", q);
    }

    [Fact]
    public void EmptyAndHierarchyFilters_AreSkipped_MultiColumnFiltersWarn()
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"));
        r.Filters.Add(Fixtures.Filter(Basic("Region", "In", "[]")));
        r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#basic","target":{"table":"Sales","hierarchy":"H","hierarchyLevel":"L"},"operator":"In","values":["a"]}"""));
        r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#hierarchy","target":[{"table":"Sales","column":"Region"},{"table":"Sales","column":"Month Name"}],"filterType":9,"hierarchyData":[{"operator":"Selected","value":"East"}]}"""));
        r.Filters.Add(Fixtures.Filter("""{"target":[{"table":"Sales","column":"Region"}],"filterType":6,"values":[]}"""));
        var plan = Build(r);
        Assert.Equal("Multi-column filter on 'Sales'[Region] > 'Sales'[Month Name] is not supported; ignored.", Assert.Single(plan.Warnings));
        Assert.DoesNotContain("VAR _f", plan.BuildQuery());
    }

    [Fact]
    public void MeasureFilters_ReuseVisualMeasures_AndAddFilterOnlyMeasures()
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"), Samples.Measure("Sales", "Total"));
        r.Filters.Add(Fixtures.Filter(Advanced("""{"table":"Sales","measure":"Total"}""", "And", """[{"operator":"GreaterThan","value":100}]""")));
        r.Filters.Add(Fixtures.Filter(Advanced("""{"table":"Sales","measure":"Count"}""", "Or",
            """[{"operator":"LessThan","value":"5"},{"operator":"Is","value":true},{"operator":"None"}]""")));
        r.Filters.Add(Fixtures.Filter(Advanced("""{"table":"Sales","column":"Amount","aggregationFunction":"Min"}""", "And", """[{"operator":"IsBlank"}]""")));
        var plan = Build(r);
        var q = plan.BuildQuery();

        Assert.Contains("VAR _vf = FILTER(KEEPFILTERS(SUMMARIZECOLUMNS('Sales'[Region], \"m_0\", [Total], \"vf_0\", IGNORE([Count]), \"vf_1\", IGNORE(MIN('Sales'[Amount])))), " +
                        "[m_0] > 100 && ([vf_0] < 5 || [vf_0] = TRUE()) && ISBLANK([vf_1]))", q);
        Assert.Contains("VAR _summ = SUMMARIZECOLUMNS('Sales'[Region], _vf, \"m_0\", [Total])", q);
        Assert.Equal(new[] { "[Total] > 100", "[Count] < 5 || [Count] = TRUE()", "ISBLANK(MIN('Sales'[Amount]))" }, plan.MeasureFilters);
    }

    [Fact]
    public void MeasureFilterWithAnUnsupportedCondition_IsIgnoredWithAWarning()
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"), Samples.Measure("Sales", "Total"));
        r.Filters.Add(Fixtures.Filter(Advanced("""{"table":"Sales","measure":"Total"}""", "And",
            """[{"operator":"GreaterThan","value":1},{"operator":"Between","value":2}]""")));
        var plan = Build(r);
        Assert.Equal("Filter on [Total] uses unsupported condition 'Between'; filter ignored.", Assert.Single(plan.Warnings));
        Assert.Empty(plan.MeasureFilters);
        Assert.DoesNotContain("_vf", plan.BuildQuery());
    }

    [Fact]
    public void UnknownFilterMeasure_FailsTheExport()
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"));
        r.Filters.Add(Fixtures.Filter(Advanced("""{"table":"Sales","measure":"Nope"}""", "And", """[{"operator":"GreaterThan","value":1}]""")));
        var ex = Assert.Throws<ExportRequestException>(() => Build(r));
        Assert.Contains("Filter measure [Nope] was not found", ex.Message);
    }

    // ---- Dates: the SDK sends the browser's local wall time as a UTC instant ----

    [Fact]
    public void DateFilterValues_AreReadInTheBrowsersTimeZone()
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"), Samples.Measure("Sales", "Total"));
        r.TimeZone = "America/Chicago";
        // Local midnight in winter (UTC-6) and summer (UTC-5); a wall time without an offset.
        r.Filters.Add(Fixtures.Filter(Basic("Order Date", "In", """["2024-02-01T06:00:00.000Z", "2024-07-01T05:00:00.000Z", "2024-03-01T00:00:00"]""")));
        r.Filters.Add(Fixtures.Filter(Advanced("""{"table":"Sales","column":"Order Date"}""", "And",
            """[{"operator":"GreaterThanOrEqual","value":"2024-01-02T09:04:05.000Z"},{"operator":"LessThan","value":"2024-12-31T12:00:00+01:00"}]""")));
        r.Filters.Add(Fixtures.Filter(Advanced("""{"table":"Sales","measure":"Total"}""", "And", """[{"operator":"GreaterThan","value":"2024-03-10T06:00:00.000Z"}]""")));
        // A text column keeps its values as sent, even if they look like dates.
        r.Filters.Add(Fixtures.Filter(Basic("Region", "In", """["2024-02-01T06:00:00.000Z"]""")));
        var q = Build(r).BuildQuery();

        Assert.Contains("VAR _f0 = TREATAS({DATE(2024, 2, 1), DATE(2024, 7, 1), DATE(2024, 3, 1)}, 'Sales'[Order Date])", q);
        Assert.Contains("VAR _f1 = FILTER(ALL('Sales'[Order Date]), 'Sales'[Order Date] >= (DATE(2024, 1, 2) + TIME(3, 4, 5)) && 'Sales'[Order Date] < (DATE(2024, 12, 31) + TIME(5, 0, 0)))", q);
        Assert.Contains("VAR _f2 = TREATAS({\"2024-02-01T06:00:00.000Z\"}, 'Sales'[Region])", q);
        Assert.Contains("[m_0] > DATE(2024, 3, 10)", q);
    }

    [Theory]
    [InlineData("Asia/Kolkata", "2024-01-31T18:30:00.000Z", "DATE(2024, 2, 1)")]
    [InlineData("Europe/Berlin", "2024-01-31T23:00:00.000Z", "DATE(2024, 2, 1)")]
    [InlineData("UTC", "2024-02-01T06:00:00.000Z", "(DATE(2024, 2, 1) + TIME(6, 0, 0))")]
    [InlineData(null, "2024-02-01T06:00:00.000Z", "(DATE(2024, 2, 1) + TIME(6, 0, 0))")]
    public void DateFilterValues_WithoutOrWithOtherTimeZones(string? zone, string value, string literal)
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"));
        r.TimeZone = zone;
        r.Filters.Add(Fixtures.Filter(Basic("Order Date", "In", $"[\"{value}\"]")));
        Assert.Contains($"TREATAS({{{literal}}}, 'Sales'[Order Date])", Build(r).BuildQuery());
    }

    [Fact]
    public void UnknownTimeZone_FailsOnlyWhenADateNeedsIt()
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"));
        r.TimeZone = "Not/AZone";
        r.Filters.Add(Fixtures.Filter(Basic("Region", "In", """["East"]""")));
        r.Filters.Add(Fixtures.Filter(Basic("Order Date", "In", """["2024-03-01T00:00:00"]""")));
        Assert.Contains("DATE(2024, 3, 1)", Build(r).BuildQuery());

        r.Filters.Add(Fixtures.Filter(Basic("Order Date", "In", """["2024-02-01T06:00:00.000Z"]""")));
        var ex = Assert.Throws<ExportRequestException>(() => Build(r));
        Assert.Contains("Unknown time zone 'Not/AZone'", ex.Message);
    }

    // ---- Dynamic M query parameters (DqParamTest: RegionParam single-select, CategoryParam multi-select) ----

    private static readonly Lazy<Microsoft.AnalysisServices.Tabular.Model> DqModel = new(() => Fixtures.Model("DqParamTest"));

    private static DaxPlan BuildDq(params string[] filters)
    {
        var r = Samples.Request(Samples.Column("Sales", "Product"), Samples.Measure("Sales", "Total Amount"));
        foreach (var f in filters) r.Filters.Add(Fixtures.Filter(f));
        return new DaxQueryBuilder().Build(r, DqModel.Value);
    }

    private static string Region(string op, string values) =>
        $$"""{"$schema":"http://powerbi.com/product/schema#basic","target":{"table":"RegionPicker","column":"RegionChoice"},"filterType":1,"operator":"{{op}}","values":{{values}},"requireSingleSelection":true}""";

    private static string Category(string op, string values) =>
        $$"""{"$schema":"http://powerbi.com/product/schema#basic","target":{"table":"CategoryPicker","column":"CategoryChoice"},"filterType":1,"operator":"{{op}}","values":{{values}}}""";

    private static string CategoryConditions(string logical, string conditions) =>
        Advanced("""{"table":"CategoryPicker","column":"CategoryChoice"}""", logical, conditions);

    [Fact]
    public void BoundColumnFilter_SetsTheMParameter_AndKeepsTheColumnFilter()
    {
        var plan = BuildDq(Region("In", """["West"]"""), Category("In", """["Bikes"]"""));
        var q = plan.BuildQuery();

        // As Power BI: parameters first; single-select scalar, multi-select always a list.
        Assert.StartsWith("DEFINE\n  MPARAMETER 'RegionParam' = \"West\"\n  MPARAMETER 'CategoryParam' = {\"Bikes\"}\n", q);
        Assert.Contains("VAR _f0 = TREATAS({\"West\"}, 'RegionPicker'[RegionChoice])", q);
        Assert.Contains("VAR _f1 = TREATAS({\"Bikes\"}, 'CategoryPicker'[CategoryChoice])", q);
        Assert.Equal(new[] { "RegionParam = \"West\"", "CategoryParam = {\"Bikes\"}" }, plan.Parameters.Select(p => p.ToString()));
        Assert.Equal("'RegionPicker'[RegionChoice]", plan.Parameters[0].ColumnRef);
    }

    [Fact]
    public void MultiSelectParameter_TakesIsConditionsJoinedByOr_AndIntersectsFilters()
    {
        var or = BuildDq(CategoryConditions("Or", """[{"operator":"Is","value":"Bikes"},{"operator":"Is","value":"Clothing"}]"""));
        Assert.Equal("CategoryParam = {\"Bikes\", \"Clothing\"}", Assert.Single(or.Parameters).ToString());

        var both = BuildDq(Category("In", """["Bikes", "Clothing"]"""), Category("In", """["Clothing", "Accessories"]"""));
        Assert.Equal("CategoryParam = {\"Clothing\"}", Assert.Single(both.Parameters).ToString());

        var ex = Assert.Throws<ExportRequestException>(() => BuildDq(Category("In", """["Bikes"]"""), Category("In", """["Clothing"]""")));
        Assert.Contains("no value in common", ex.Message);
    }

    [Fact]
    public void NoSelectionOnABoundColumn_KeepsTheParameterDefault()
    {
        var plan = BuildDq(Region("All", "[]"), Category("In", "[]"), Category("NotIn", "[]"), CategoryConditions("And", "[]"));
        Assert.Empty(plan.Parameters);
        Assert.DoesNotContain("MPARAMETER", plan.BuildQuery());
    }

    private static string SelectAll(string table, string column) =>
        $$"""{"$schema":"http://powerbi.com/product/schema#basic","target":{"table":"{{table}}","column":"{{column}}"},"filterType":1,"operator":"All","values":[],"requireSingleSelection":false,"selectAll":true}""";

    [Fact]
    public void SelectAll_PassesTheBindingsSelectAllValue()
    {
        // As traced from Power BI after clicking "Select all" on the multi-select slicer.
        var plan = BuildDq(SelectAll("CategoryPicker", "CategoryChoice"));
        var q = plan.BuildQuery();
        Assert.Equal("CategoryParam = {\"__SelectAll__\"}", Assert.Single(plan.Parameters).ToString());
        Assert.True(plan.Parameters[0].SelectAll); // not checked against the column's values
        Assert.StartsWith("DEFINE\n  MPARAMETER 'CategoryParam' = {\"__SelectAll__\"}\n", q);
        Assert.DoesNotContain("TREATAS", q);

        // Another filter on the column narrows "Select all", in either order.
        var narrowed = BuildDq(SelectAll("CategoryPicker", "CategoryChoice"), Category("In", """["Bikes"]"""));
        Assert.Equal("CategoryParam = {\"Bikes\"}", Assert.Single(narrowed.Parameters).ToString());
        Assert.False(narrowed.Parameters[0].SelectAll);
        Assert.Equal("CategoryParam = {\"Bikes\"}", Assert.Single(BuildDq(Category("In", """["Bikes"]"""), SelectAll("CategoryPicker", "CategoryChoice")).Parameters).ToString());

        // On a column no parameter is bound to, "Select all" is no filter at all.
        var plain = BuildDq(SelectAll("Sales", "Category"));
        Assert.Empty(plain.Parameters);
        Assert.DoesNotContain("TREATAS", plain.BuildQuery());
    }

    [Fact]
    public void SelectAll_WithoutASelectAllValueOnTheBinding_FailsTheExport()
    {
        var ex = Assert.Throws<ExportRequestException>(() => BuildDq(SelectAll("RegionPicker", "RegionChoice")));
        Assert.Contains("has no \"Select all\" value", ex.Message);
    }

    [Fact]
    public void DateAndDateTimeParameters_TakeTheBrowsersWallTime()
    {
        // As traced from Power BI at UTC-6: the slicer's local 2024-02-01 00:00 arrives as 06:00Z
        // and Power BI sends MPARAMETER 'FromDateParam' = DATE(2024, 2, 1).
        var r = Samples.Request(Samples.Column("Sales", "Product"), Samples.Measure("Sales", "Total Amount"));
        r.TimeZone = "America/Chicago";
        r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#basic","target":{"table":"DatePicker","column":"FromDate"},"filterType":1,"operator":"In","values":["2024-02-01T06:00:00.000Z"],"requireSingleSelection":true}"""));
        r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#basic","target":{"table":"DateTimePicker","column":"ToDateTime"},"filterType":1,"operator":"In","values":["2024-04-04T05:00:01.000Z"],"requireSingleSelection":true}"""));
        var plan = new DaxQueryBuilder().Build(r, DqModel.Value);

        Assert.Equal(new[] { "FromDateParam = DATE(2024, 2, 1)", "ToDateTimeParam = (DATE(2024, 4, 4) + TIME(0, 0, 1))" },
            plan.Parameters.Select(p => p.ToString()));
        Assert.Contains("TREATAS({DATE(2024, 2, 1)}, 'DatePicker'[FromDate])", plan.BuildQuery());
    }

    [Theory]
    [InlineData("region-two-values", "takes a single value")]
    [InlineData("category-notin", "excludes values")]
    [InlineData("category-greaterthan", "uses condition 'GreaterThan'")]
    [InlineData("category-is-and", "no value in common")]
    public void SelectionsPowerBICantBindToAParameter_FailTheExport(string shape, string message)
    {
        var filter = shape switch
        {
            "region-two-values" => Region("In", """["West", "North"]"""),
            "category-notin" => Category("NotIn", """["Bikes"]"""),
            "category-greaterthan" => CategoryConditions("And", """[{"operator":"GreaterThan","value":"B"}]"""),
            _ => CategoryConditions("And", """[{"operator":"Is","value":"Bikes"},{"operator":"Is","value":"Clothing"}]""")
        };
        var ex = Assert.Throws<ExportRequestException>(() => BuildDq(filter));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void TopN_RanksUnderTheColumnFilters()
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"), Samples.Measure("Sales", "Total"));
        r.Filters.Add(Fixtures.Filter(Basic("Is Online", "In", "[false]")));
        r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#topN","target":{"table":"Sales","column":"Region"},"filterType":5,"operator":"Bottom","itemCount":2,"orderBy":{"table":"Sales","column":"Amount","aggregationFunction":"Sum"}}"""));
        r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#topN","target":{"table":"Sales","column":"Region"},"filterType":5,"operator":"Top","itemCount":0,"orderBy":{"table":"Sales","measure":"Total"}}"""));
        var plan = Build(r);
        var q = plan.BuildQuery();
        Assert.Contains("VAR _tn0 = TOPN(2, SUMMARIZECOLUMNS('Sales'[Region], _f0, \"tn\", SUM('Sales'[Amount])), [tn], 1)", q);
        Assert.Contains("VAR _summ = SUMMARIZECOLUMNS('Sales'[Region], _f0, _tn0, \"m_0\", [Total])", q);
        Assert.Equal("Bottom 2 'Sales'[Region] by SUM('Sales'[Amount])", Assert.Single(plan.MeasureFilters));
    }

    // ---- Report measures ----

    [Fact]
    public void ReportMeasureOnAMissingTable_FailsTheExport()
    {
        var report = Samples.Pbir(("reportExtensions.json",
            """{"entities":[{"name":"Gone","measures":[{"name":"RM","expression":"[Total] * 2"}]}]}"""));
        var ex = Assert.Throws<ExportRequestException>(() => Build(Samples.Request(Samples.Measure("Sales", "RM")), report));
        Assert.Equal("Report measure [RM] is defined on table 'Gone', which is not in the semantic model.", ex.Message);
    }

    [Fact]
    public void ReportMeasures_UsedInFiltersAndSort_AreDefined()
    {
        var report = Samples.Pbir(("reportExtensions.json",
            """{"entities":[{"name":"sales","measures":[{"name":"Double","expression":"[Total] * 2","formatString":"0.0"},{"name":"Rank Key","expression":"[Double] + 1"}]}]}"""));
        var r = Samples.Request(Samples.Column("Sales", "Region"), Samples.Measure("Sales", "Double"));
        r.Sort.Add(new ExportSort { Table = "Sales", Measure = "Rank Key", Direction = "Descending" });
        var plan = Build(r, report);
        var q = plan.BuildQuery();

        Assert.Equal(new[] { "Double", "Rank Key" }, plan.ReportMeasures);
        Assert.StartsWith("DEFINE\n  MEASURE 'Sales'[Double] =\n    [Total] * 2\n  MEASURE 'Sales'[Rank Key] =\n    [Double] + 1\n", q);
        Assert.Contains("\"c1\", FORMAT([m_0], \"0.0\")", q);
        Assert.EndsWith("ORDER BY [s0] DESC", q);
    }
}
