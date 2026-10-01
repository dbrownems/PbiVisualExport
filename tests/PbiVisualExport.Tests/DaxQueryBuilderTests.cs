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
        r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#relativeDate","target":{"table":"Sales","column":"New Relative"},"operator":0,"timeUnitsCount":3,"timeUnitType":0,"filterType":4}"""));
        // Inactive filters (nothing selected) and known columns don't count.
        r.Filters.Add(Fixtures.Filter("""{"$schema":"http://powerbi.com/product/schema#relativeDate","target":{"table":"Sales","column":"Unset Relative"},"operator":0,"timeUnitType":0,"filterType":4}"""));
        r.Filters.Add(Fixtures.Filter(Basic("Unset", "In", "[]")));
        r.Filters.Add(Fixtures.Filter(Basic("All Selected", "All", """["x"]""")));
        r.Filters.Add(Fixtures.Filter(Advanced("""{"table":"Sales","column":"No Conditions"}""", "And", "[]")));
        r.Filters.Add(Fixtures.Filter(Basic("region", "In", """["West"]""")));

        Assert.Equal(new[]
        {
            new UnresolvedField("'Sales'[New Column]", null, false),
            new UnresolvedField("'Sales'[New Advanced]", null, false),
            new UnresolvedField("'Sales'[New TopN]", null, false),
            new UnresolvedField("'Sales'[New Relative]", null, false)
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
    public void EmptyFilters_AreSkipped()
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"));
        r.Filters.Add(Fixtures.Filter(Basic("Region", "In", "[]")));
        r.Filters.Add(Fixtures.Filter(Hierarchy("[]")));
        r.Filters.Add(Fixtures.Filter("""{"target":[{"table":"Sales","column":"Region"}],"filterType":6,"operator":"In","values":[]}"""));
        // Only Inherited nodes: nothing is selected or deselected.
        r.Filters.Add(Fixtures.Filter(Hierarchy("""[{"operator":"Inherited","value":"East"}]""")));
        var plan = Build(r);
        Assert.Empty(plan.Warnings);
        Assert.DoesNotContain("VAR _f", plan.BuildQuery());
    }

    // ---- Hierarchy levels, hierarchy slicers and tuple filters ----

    private const string RegionMonth = """[{"table":"Sales","column":"Region"},{"table":"Sales","column":"Month Name"}]""";

    private static string Hierarchy(string data, string targets = RegionMonth) =>
        $$"""{"$schema":"http://powerbi.com/product/schema#hierarchy","target":{{targets}},"filterType":9,"hierarchyData":{{data}}}""";

    private static string Tuple(string op, string values, string targets = RegionMonth) =>
        $$"""{"$schema":"http://powerbi.com/product/schema#tuple","target":{{targets}},"filterType":6,"operator":"{{op}}","values":{{values}}}""";

    private static string Level(string hierarchy, string level, string table = "Sales") =>
        $$"""{"table":"{{table}}","hierarchy":"{{hierarchy}}","hierarchyLevel":"{{level}}"}""";

    [Fact]
    public void HierarchyLevelTargets_FilterTheLevelsColumn()
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"), Samples.Measure("Sales", "Total"));
        r.Filters.Add(Fixtures.Filter($$"""{"$schema":"http://powerbi.com/product/schema#basic","target":{{Level("Geo", "Area")}},"filterType":1,"operator":"In","values":["East"]}"""));
        r.Filters.Add(Fixtures.Filter(Advanced(Level("geo", "month"), "And", """[{"operator":"IsNot","value":"March"}]""")));
        // Auto date/time: the hierarchy is named after the date column; its levels are the LocalDateTable's.
        r.Filters.Add(Fixtures.Filter($$"""{"$schema":"http://powerbi.com/product/schema#basic","target":{{Level("Order Date", "Year")}},"filterType":1,"operator":"NotIn","values":[2023]}"""));
        r.Filters.Add(Fixtures.Filter($$$"""{"$schema":"http://powerbi.com/product/schema#topN","target":{{{Level("Geo", "Area")}}},"filterType":5,"operator":"Top","itemCount":3,"orderBy":{"table":"Sales","measure":"Total"}}"""));
        r.Filters.Add(Fixtures.Filter(Advanced("""{"table":"Sales","hierarchy":"Geo","hierarchyLevel":"Month","aggregationFunction":"CountNonNull"}""", "And",
            """[{"operator":"GreaterThan","value":1}]""")));
        var plan = Build(r);
        var q = plan.BuildQuery();

        Assert.Empty(plan.Warnings);
        Assert.Contains("VAR _f0 = TREATAS({\"East\"}, 'Sales'[Region])", q);
        Assert.Contains("VAR _f1 = FILTER(ALL('Sales'[Month Name]), 'Sales'[Month Name] <> \"March\")", q);
        Assert.Contains("VAR _f2 = FILTER(ALL('LocalDateTable_1'[Year]), NOT('LocalDateTable_1'[Year] IN {2023}))", q);
        Assert.Contains("VAR _tn0 = TOPN(3, SUMMARIZECOLUMNS('Sales'[Region], _f0, _f1, _f2, \"tn\", [Total]), [tn], 0)", q);
        Assert.Contains("\"vf_0\", IGNORE(COUNTA('Sales'[Month Name]))", q);
        Assert.Equal(new[] { "Top 3 'Sales'[Region] by [Total]", "COUNTA('Sales'[Month Name]) > 1" }, plan.MeasureFilters);
    }

    [Fact]
    public void UnknownHierarchyLevel_IsUnresolved_AndFailsTheExport()
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"));
        r.Filters.Add(Fixtures.Filter($$"""{"$schema":"http://powerbi.com/product/schema#basic","target":{{Level("Geo", "Nope")}},"filterType":1,"operator":"In","values":["a"]}"""));
        r.Filters.Add(Fixtures.Filter(Hierarchy("""[{"operator":"Selected","value":"East"}]""", $"[{Level("Gone", "Area")}]")));
        // Inactive filters aren't checked.
        r.Filters.Add(Fixtures.Filter($$"""{"$schema":"http://powerbi.com/product/schema#basic","target":{{Level("Unused", "L")}},"filterType":1,"operator":"In","values":[]}"""));

        Assert.Equal(new[] { "'Sales'[Geo].[Nope]", "'Sales'[Gone].[Area]" },
            DaxQueryBuilder.FindUnresolvedFields(r, Model).Select(u => u.Label));
        var ex = Assert.Throws<ExportRequestException>(() => Build(r));
        Assert.Equal("Hierarchy level 'Sales'[Geo].[Nope] was not found in the semantic model.", ex.Message);
    }

    private static ExportField LevelField(string hierarchy, string level, string? aggregation = null) =>
        new() { Table = "Sales", Hierarchy = hierarchy, HierarchyLevel = level, Aggregation = aggregation, Role = "Values" };

    [Fact]
    public void HierarchyLevelFields_ExportTheLevelsColumn_HeadedByTheLevel()
    {
        // A level always wins over a column sent alongside it.
        var area = LevelField("Geo", "Area");
        area.Column = "Amount";
        var r = Samples.Request(area, LevelField("geo", "month"), LevelField("Order Date", "Year"), LevelField("Order Date", "Year", "CountNonNull"),
            Samples.Measure("Sales", "Total"));
        r.Sort.Add(new ExportSort { Table = "Sales", Hierarchy = "Geo", HierarchyLevel = "Month", Direction = "Descending" });
        r.Sort.Add(new ExportSort { Table = "Sales", Hierarchy = "Order Date", HierarchyLevel = "Year" });

        Assert.Empty(DaxQueryBuilder.FindUnresolvedFields(r, Model));
        var plan = Build(r);
        var q = plan.BuildQuery();

        Assert.Empty(plan.Warnings);
        Assert.Equal(new[] { "Area", "Month", "Year", "Count of Year", "Total" }, plan.Columns.Select(c => c.Header));
        Assert.Contains("SUMMARIZECOLUMNS('Sales'[Region], 'Sales'[Month Name], 'LocalDateTable_1'[Year], 'Sales'[Month Number],", q);
        Assert.Contains("\"m_0\", COUNTA('LocalDateTable_1'[Year])", q);
        // Month is ordered by its Sort By Column, like the column itself.
        Assert.Contains("\"s0\", 'Sales'[Month Number]", q);
        Assert.EndsWith("ORDER BY [s0] DESC, [c2] ASC", q);
        // The request itself isn't changed.
        Assert.Equal("Geo", r.Fields[0].Hierarchy);
    }

    [Fact]
    public void ImplicitAggregations_AreComputedAndHeadedAsPowerBiDoes()
    {
        // Names as getDataFields reports them: "Count" is Count (Distinct), "CountNonNull" is Count.
        string[] names = { "Sum", "Avg", "Count", "Min", "Max", "CountNonNull", "Median", "StandardDeviation", "Variance" };
        var r = Samples.Request(new[] { Samples.Column("Sales", "Region") }
            .Concat(names.Select(n => new ExportField { Table = "Sales", Column = "Amount", Aggregation = n, Role = "Values" }))
            .Append(new ExportField { Table = "Sales", Column = "Is Online", Aggregation = "CountNonNull", Role = "Values" })
            .ToArray());
        var plan = Build(r);
        var q = plan.BuildQuery();

        Assert.Equal(new[]
        {
            "Region", "Sum of Amount", "Average of Amount", "Count of Amount", "Min of Amount", "Max of Amount", "Count of Amount",
            "Median of Amount", "Standard deviation of Amount", "Variance of Amount", "Count of Is Online"
        }, plan.Columns.Select(c => c.Header));
        string[] dax =
        {
            "SUM('Sales'[Amount])", "AVERAGE('Sales'[Amount])", "DISTINCTCOUNT('Sales'[Amount])", "MIN('Sales'[Amount])",
            "MAX('Sales'[Amount])", "COUNTA('Sales'[Amount])", "MEDIAN('Sales'[Amount])", "STDEV.P('Sales'[Amount])",
            "VAR.P('Sales'[Amount])", "COUNTA('Sales'[Is Online])"
        };
        for (var i = 0; i < dax.Length; i++) Assert.Contains($"\"m_{i}\", {dax[i]}", q);
        // Counts are whole numbers: the column's format string isn't applied to them.
        Assert.Contains("\"c1\", FORMAT([m_0], \"#,0.00\")", q);
        Assert.Contains("\"c3\", [m_2]", q);
        Assert.Contains("\"c6\", [m_5]", q);
        Assert.Contains("\"c8\", FORMAT([m_7], \"#,0.00\")", q);
    }

    [Fact]
    public void UnknownAggregation_FailsTheExport()
    {
        var field = Samples.Request(Samples.Column("Sales", "Region"),
            new ExportField { Table = "Sales", Column = "Amount", Aggregation = "Mode", Role = "Values" });
        Assert.Equal("The visual aggregates 'Sales'[Amount] with Mode, which the export can't compute. Export aborted.",
            Assert.Throws<ExportRequestException>(() => Build(field)).Message);

        // A value filter on it would be dropped, over-exporting.
        var filter = Samples.Request(Samples.Column("Sales", "Region"));
        filter.Filters.Add(Fixtures.Filter(Advanced("""{"table":"Sales","column":"Amount","aggregationFunction":"Mode"}""", "And",
            """[{"operator":"GreaterThan","value":1}]""")));
        Assert.Contains("uses the aggregation Mode, which the export can't compute",
            Assert.Throws<ExportRequestException>(() => Build(filter)).Message);
    }

    [Fact]
    public void UnknownHierarchyLevelField_IsUnresolved_AndUnknownLevelSortKeyWarns()
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"), LevelField("Geo", "Nope"));
        Assert.Equal(new[] { "'Sales'[Geo].[Nope]" }, DaxQueryBuilder.FindUnresolvedFields(r, Model).Select(u => u.Label));

        var sorted = Samples.Request(Samples.Column("Sales", "Region"));
        sorted.Sort.Add(new ExportSort { Table = "Sales", Hierarchy = "Geo", HierarchyLevel = "Nope" });
        Assert.Empty(DaxQueryBuilder.FindUnresolvedFields(sorted, Model));
        Assert.Contains("Sort hierarchy level Geo.Nope ASC not found in the model; ignored.", Build(sorted).Warnings);
    }

    [Fact]
    public void FieldThatNamesNothing_FailsTheExport_AndFieldWithoutTableWarns()
    {
        // Silently dropping a field the visual groups on would change the grain.
        var ex = Assert.Throws<ExportRequestException>(() =>
            Build(Samples.Request(Samples.Column("Sales", "Region"), new ExportField { Table = "Sales", Role = "Rows" })));
        Assert.Contains("on table 'Sales' (Rows) is not a column, hierarchy level or measure", ex.Message);

        var plan = Build(Samples.Request(Samples.Column("Sales", "Region"), new ExportField { Role = "Values" }));
        Assert.Equal(new[] { "Region" }, plan.Columns.Select(c => c.Header));
        Assert.Contains("A field of the visual (Values) names no table; it was not exported.", plan.Warnings);
    }

    [Fact]
    public void HierarchySlicer_SelectedNodes_PickValuesAndBranches()
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"));
        r.Filters.Add(Fixtures.Filter(Hierarchy("""
            [{"operator":"Selected","value":"East"},
             {"operator":"Inherited","value":"West","children":[{"operator":"Selected","value":"January"}]},
             {"operator":"Selected","value":"North","children":[{"operator":"NotSelected","value":"March"}]},
             {"value":"South","children":[{"operator":"Inherited","value":"May"}]}]
            """)));
        var q = Build(r).BuildQuery();
        Assert.Contains("VAR _f0 = FILTER(ALL('Sales'[Region], 'Sales'[Month Name]), ('Sales'[Region] IN {\"East\"} || " +
            "('Sales'[Region] IN {\"West\"} && 'Sales'[Month Name] IN {\"January\"}) || " +
            "('Sales'[Region] IN {\"North\"} && NOT('Sales'[Month Name] IN {\"March\"}))))", q);
    }

    [Fact]
    public void HierarchySlicer_AfterSelectAll_NotSelectedNodesExclude()
    {
        // LargeExportReport slicer3d: Select all, then clear North › Electronics (and all of South).
        var r = Samples.Request(Samples.Column("Sales", "Region"));
        r.Filters.Add(Fixtures.Filter(Hierarchy("""
            [{"operator":"Inherited","value":"North","children":[{"operator":"NotSelected","value":"March"}]},
             {"operator":"NotSelected","value":"South"}]
            """, $"[{Level("Geo", "Area")},{Level("Geo", "Month")}]")));
        var q = Build(r).BuildQuery();
        Assert.Contains("VAR _f0 = FILTER(ALL('Sales'[Region], 'Sales'[Month Name]), (NOT('Sales'[Region] IN {\"North\", \"South\"}) || " +
            "('Sales'[Region] IN {\"North\"} && NOT('Sales'[Month Name] IN {\"March\"}))))", q);
    }

    [Fact]
    public void HierarchySlicer_EdgeCases()
    {
        var tooDeep = Samples.Request(Samples.Column("Sales", "Region"));
        tooDeep.Filters.Add(Fixtures.Filter(Hierarchy("""[{"operator":"Selected","value":"East","children":[{"operator":"NotSelected","value":"March"}]}]""",
            """[{"table":"Sales","column":"Region"}]""")));
        // A level below the last target that deselects: 400, not a guess.
        Assert.Contains("more levels than its 1 column(s)", Assert.Throws<ExportRequestException>(() => Build(tooDeep)).Message);

        var mixed = Samples.Request(Samples.Column("Sales", "Region"));
        mixed.Filters.Add(Fixtures.Filter(Hierarchy("""[{"operator":"Inherited","value":"East","children":[{"operator":"Selected","value":"March","children":[]}]},{"operator":"NotSelected","value":"West"}]""")));
        Assert.Contains("both selects and deselects", Assert.Throws<ExportRequestException>(() => Build(mixed)).Message);

        // Explicit nodes below Inherited ones set the mode; a deselected node's children can reselect.
        var nested = Samples.Request(Samples.Column("Sales", "Region"));
        nested.Filters.Add(Fixtures.Filter(Hierarchy("""[{"operator":"Inherited","value":"East","children":[{"operator":"Selected","value":"March"}]}]""")));
        nested.Filters.Add(Fixtures.Filter(Hierarchy("""[{"operator":"NotSelected","value":"West","children":[{"operator":"Selected","value":"May"}]}]""")));
        var q = Build(nested).BuildQuery();
        Assert.Contains("VAR _f0 = FILTER(ALL('Sales'[Region], 'Sales'[Month Name]), ('Sales'[Region] IN {\"East\"} && 'Sales'[Month Name] IN {\"March\"}))", q);
        Assert.Contains("VAR _f1 = FILTER(ALL('Sales'[Region], 'Sales'[Month Name]), (NOT('Sales'[Region] IN {\"West\"}) || ('Sales'[Region] IN {\"West\"} && 'Sales'[Month Name] IN {\"May\"})))", q);
    }

    [Fact]
    public void HierarchySlicer_AcrossTables_CrossJoinsTheLevels()
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"));
        r.Filters.Add(Fixtures.Filter(Hierarchy("""[{"operator":"Selected","value":"East","children":[]},{"operator":"Inherited","value":null,"children":[{"operator":"Selected","value":"x"}]}]""",
            """[{"table":"Sales","column":"Region"},{"table":"O'Brien","column":"a]b"}]""")));
        var q = Build(r).BuildQuery();
        Assert.Contains("VAR _f0 = FILTER(CROSSJOIN(ALL('Sales'[Region]), ALL('O''Brien'[a]]b])), ('Sales'[Region] IN {\"East\"} || " +
            "('Sales'[Region] IN {BLANK()} && 'O''Brien'[a]]b] IN {\"x\"})))", q);
    }

    [Fact]
    public void TupleFilters_InAndNotIn()
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"));
        r.Filters.Add(Fixtures.Filter(Tuple("In", """[[{"value":"East"},{"value":"January"}],[{"value":"West"},{"value":"March"}]]""")));
        r.Filters.Add(Fixtures.Filter(Tuple("NotIn", """[[{"value":"North"},{"value":"May"}]]""")));
        r.Filters.Add(Fixtures.Filter(Tuple("In", """[[true, 2]]""", """[{"table":"Sales","column":"Is Online"},{"table":"Sales","column":"Month Number"}]""")));
        r.Filters.Add(Fixtures.Filter(Tuple("In", """[[{"value":"East"}]]""", """[{"table":"Sales","column":"Region"}]""")));
        var q = Build(r).BuildQuery();

        Assert.Contains("VAR _f0 = TREATAS({(\"East\", \"January\"), (\"West\", \"March\")}, 'Sales'[Region], 'Sales'[Month Name])", q);
        Assert.Contains("VAR _f1 = FILTER(ALL('Sales'[Region], 'Sales'[Month Name]), NOT(('Sales'[Region], 'Sales'[Month Name]) IN {(\"North\", \"May\")}))", q);
        Assert.Contains("VAR _f2 = TREATAS({(TRUE(), 2)}, 'Sales'[Is Online], 'Sales'[Month Number])", q);
        Assert.Contains("VAR _f3 = TREATAS({\"East\"}, 'Sales'[Region])", q);
    }

    [Theory]
    [InlineData("""{"target":[{"table":"Sales","column":"Region"},{"table":"Sales","measure":"Total"}],"filterType":6,"operator":"In","values":[["a",1]]}""", "which is not a column or hierarchy level")]
    [InlineData("""{"target":[{"table":"Sales","column":"Region"},{"table":"Sales","column":"Month Name"}],"filterType":6,"operator":"In","values":[["a"]]}""", "doesn't match its 2 column(s)")]
    [InlineData("""{"target":[{"table":"Sales","column":"Region"}],"filterType":6,"operator":"Contains","values":[["a"]]}""", "uses operator 'Contains'")]
    [InlineData("""{"target":[{"table":"Sales","column":"Region"}],"filterType":9,"hierarchyData":[{"operator":"Excluded","value":"a"}]}""", "uses operator 'Excluded'")]
    [InlineData("""{"target":[{"table":"Sales","column":"Region"}],"filterType":9,"hierarchyData":[{"operator":"Selected","keyValues":["a"]}]}""", "only key values")]
    public void MultiColumnFiltersThatCantBeTranslated_FailTheExport(string filter, string message)
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"));
        r.Filters.Add(Fixtures.Filter(filter));
        Assert.Contains(message, Assert.Throws<ExportRequestException>(() => Build(r)).Message);
    }

    [Theory]
    [InlineData("""{"$schema":"http://powerbi.com/product/schema#advanced","target":{"table":"Sales","column":"Region"},"filterType":0,"logicalOperator":"And","conditions":[{"operator":"Is","value":"East"},{"operator":"Between","value":"W"}]}""", "the condition 'Between' isn't supported")]
    [InlineData("""{"$schema":"http://powerbi.com/product/schema#advanced","target":{"table":"Sales","column":"Region"},"filterType":0,"logicalOperator":"Or","conditions":[{"operator":"Between","value":"W"},{"operator":"Is","value":"East"}]}""", "the condition 'Between' isn't supported")]
    [InlineData("""{"$schema":"http://powerbi.com/product/schema#includeExclude","target":{"table":"Sales","column":"Region"},"filterType":3,"isExclude":true,"values":["East"]}""", "includes or excludes specific data points")]
    [InlineData("""{"$schema":"http://powerbi.com/product/schema#basic","target":{"table":"Sales","column":"Region"},"filterType":1,"operator":"Contains","values":["E"]}""", "the operator 'Contains' isn't supported")]
    [InlineData("""{"$schema":"http://powerbi.com/product/schema#notSupported","target":{"table":"Sales","column":"Region"},"filterType":2,"message":"x"}""", "doesn't expose to the API")]
    [InlineData("""{"$schema":"http://powerbi.com/product/schema#topN",    "target":{"table":"Sales","column":"Region"},"filterType":5,"operator":"Top","itemCount":3,"orderBy":{"table":"Sales","column":"Amount"}}""", "The Top N filter on 'Sales'[Region]")]
        [InlineData("""{"$schema":"http://powerbi.com/product/schema#advanced","target":{"table":"Sales","column":"Amount","aggregationFunction":"Sum"},"filterType":0,"logicalOperator":"And","conditions":[{"operator":"Between","value":"1"}]}""", "the condition 'Between' isn't supported")]
    public void ColumnFiltersThatCantBeTranslated_FailTheExport(string filter, string message)
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"), Samples.Measure("Sales", "Total"));
        r.Filters.Add(Fixtures.Filter(filter));
        var ex = Assert.Throws<ExportRequestException>(() => Build(r));
        Assert.Contains(message, ex.Message);
        Assert.Contains("Export aborted", ex.Message);
    }

    // ---- Relative date/time filters (UTC window, evaluated at request time) ----

    private static readonly DateTimeOffset Monday = new(2026, 7, 20, 14, 37, 25, TimeSpan.Zero);

    private static string Rel(string op, string count, string unit, string? includeToday = "true", int filterType = 4, string column = "Order Date") =>
        $$"""{"$schema":"http://powerbi.com/product/schema#{{(filterType == 7 ? "relativeTime" : "relativeDate")}}","target":{"table":"Sales","column":"{{column}}"},"operator":{{op}},"timeUnitsCount":{{count}},"timeUnitType":{{unit}}{{(includeToday is null ? "" : $",\"includeToday\":{includeToday}")}},"filterType":{{filterType}}}""";

    private static string RelQuery(string filter, DateTimeOffset now)
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"), Samples.Measure("Sales", "Total"));
        r.Filters.Add(Fixtures.Filter(filter));
        return new DaxQueryBuilder(new ManualTime { Now = now }).Build(r, Model).BuildQuery();
    }

    private static string Window(string start, string end) =>
        $"FILTER(ALL('Sales'[Order Date]), 'Sales'[Order Date] >= {start} && 'Sales'[Order Date] < {end})";

    [Theory]
    [InlineData("0", "3", "0", "true", "DATE(2026, 7, 18)", "DATE(2026, 7, 21)")]     // last 3 days, including today
    [InlineData("0", "3", "0", null, "DATE(2026, 7, 18)", "DATE(2026, 7, 21)")]       // includeToday defaults to true
    [InlineData("0", "3", "0", "false", "DATE(2026, 7, 17)", "DATE(2026, 7, 20)")]    // last 3 days, without today
    [InlineData("0", "1", "3", "true", "DATE(2026, 6, 21)", "DATE(2026, 7, 21)")]     // last 1 month
    [InlineData("0", "2", "4", "true", "DATE(2026, 5, 1)", "DATE(2026, 7, 1)")]       // last 2 calendar months
    [InlineData("0", "1", "2", "true", "DATE(2026, 7, 12)", "DATE(2026, 7, 19)")]     // last calendar week (weeks start Sunday)
    [InlineData("1", "1", "1", "true", "DATE(2026, 7, 19)", "DATE(2026, 7, 26)")]     // this week
    [InlineData("1", "1", "0", "true", "DATE(2026, 7, 20)", "DATE(2026, 7, 21)")]     // today
    [InlineData("1", "1", "5", "true", "DATE(2026, 1, 1)", "DATE(2027, 1, 1)")]       // this year
    [InlineData("2", "2", "1", "true", "DATE(2026, 7, 20)", "DATE(2026, 8, 3)")]      // next 2 weeks, including today
    [InlineData("2", "2", "1", "false", "DATE(2026, 7, 21)", "DATE(2026, 8, 4)")]     // next 2 weeks, from tomorrow
    [InlineData("2", "1", "6", "true", "DATE(2027, 1, 1)", "DATE(2028, 1, 1)")]       // next calendar year
    [InlineData("\"InLast\"", "3", "\"Days\"", "true", "DATE(2026, 7, 18)", "DATE(2026, 7, 21)")] // enum names
    public void RelativeDateFilters_BecomeAUtcWindow(string op, string count, string unit, string? includeToday, string start, string end)
    {
        var q = RelQuery(Rel(op, count, unit, includeToday), Monday);
        Assert.Contains($"VAR _f0 = {Window(start, end)}", q);
    }

    [Fact]
    public void RelativeDateFilters_ClampMonthEnds()
    {
        var q = RelQuery(Rel("0", "1", "3"), new DateTimeOffset(2026, 3, 31, 23, 59, 0, TimeSpan.Zero));
        Assert.Contains(Window("DATE(2026, 3, 1)", "DATE(2026, 4, 1)"), q);
    }

    [Fact]
    public void RelativeDateFilters_UseTheUtcDate()
    {
        // 2026-07-20 23:30 in Chicago is already 2026-07-21 in UTC; the service evaluates in UTC.
        var q = RelQuery(Rel("1", "1", "0"), new DateTimeOffset(2026, 7, 20, 23, 30, 0, TimeSpan.FromHours(-5)));
        Assert.Contains(Window("DATE(2026, 7, 21)", "DATE(2026, 7, 22)"), q);
    }

    [Theory]
    [InlineData("0", "15", "7", "(DATE(2026, 7, 20) + TIME(14, 22, 0))", "(DATE(2026, 7, 20) + TIME(14, 37, 0))")] // last 15 minutes
    [InlineData("1", "1", "8", "(DATE(2026, 7, 20) + TIME(14, 0, 0))", "(DATE(2026, 7, 20) + TIME(15, 0, 0))")]   // this hour
    [InlineData("2", "2", "8", "(DATE(2026, 7, 20) + TIME(14, 37, 0))", "(DATE(2026, 7, 20) + TIME(16, 37, 0))")] // next 2 hours
    [InlineData("0", "15", "\"Hours\"", "(DATE(2026, 7, 19) + TIME(23, 37, 0))", "(DATE(2026, 7, 20) + TIME(14, 37, 0))")] // crosses midnight
    public void RelativeTimeFilters_BecomeAUtcWindow(string op, string count, string unit, string start, string end)
    {
        var q = RelQuery(Rel(op, count, unit, includeToday: null, filterType: 7), Monday);
        Assert.Contains($"VAR _f0 = {Window(start, end)}", q);
    }

    [Theory]
    [InlineData("0", "3", "0", 4, "Region", "not a date")]
    [InlineData("0", "3", "0", 7, "Order Date", "doesn't support")]   // Days on a relative time filter
    [InlineData("0", "3", "8", 4, "Order Date", "doesn't support")]   // Hours on a relative date filter
    [InlineData("5", "3", "0", 4, "Order Date", "unknown operator")]
    [InlineData("0", "3", "42", 4, "Order Date", "unknown time unit")]
    [InlineData("0", "1.5", "0", 4, "Order Date", "whole, positive")]
    public void RelativeFiltersThatCantBeTranslated_FailTheExport(string op, string count, string unit, int filterType, string column, string message)
    {
        var ex = Assert.Throws<ExportRequestException>(() => RelQuery(Rel(op, count, unit, filterType: filterType, column: column), Monday));
        Assert.Contains(message, ex.Message);
        Assert.Contains("Export aborted", ex.Message);
    }

    [Fact]
    public void RelativeFilter_OnAnMParameterColumn_IsRefused()
    {
        var filter = """{"$schema":"http://powerbi.com/product/schema#relativeDate","target":{"table":"DatePicker","column":"FromDate"},"operator":0,"timeUnitsCount":3,"timeUnitType":0,"includeToday":true,"filterType":4}""";
        var ex = Assert.Throws<ExportRequestException>(() => BuildDq(filter));
        Assert.Contains("is a relative date filter", ex.Message);
        Assert.Contains("FromDateParam", ex.Message);
    }

    [Theory]
    [InlineData("""{"$schema":"http://powerbi.com/product/schema#advanced","target":{"table":"Sales","column":"Region"},"filterType":0,"logicalOperator":"And","conditions":[{"operator":"None"}]}""")]
    [InlineData("""{"$schema":"http://powerbi.com/product/schema#advanced","target":{"table":"Sales","column":"Region"},"filterType":0,"logicalOperator":"And","conditions":[]}""")]
    [InlineData("""{"$schema":"http://powerbi.com/product/schema#basic","target":{"table":"Sales","column":"Region"},"filterType":1,"operator":"All","values":[]}""")]
    [InlineData("""{"$schema":"http://powerbi.com/product/schema#basic","target":{"table":"Sales","column":"Region"},"filterType":1,"operator":"In","values":[]}""")]
    [InlineData("""{"$schema":"http://powerbi.com/product/schema#relativeDate","target":{"table":"Sales","column":"Order Date"},"operator":0,"timeUnitType":3,"includeToday":true,"filterType":4}""")]
    [InlineData("""{"$schema":"http://powerbi.com/product/schema#relativeDate","target":{"table":"Sales","column":"Order Date"},"operator":0,"timeUnitsCount":0,"timeUnitType":3,"filterType":4}""")]
    [InlineData("""{"$schema":"http://powerbi.com/product/schema#includeExclude","target":{"table":"Sales","column":"Region"},"filterType":3,"isExclude":true,"values":[]}""")]
    [InlineData("""{"$schema":"http://powerbi.com/product/schema#topN","target":{"table":"Sales","column":"Region"},"filterType":5,"operator":"Top","itemCount":0,"orderBy":{"table":"Sales","measure":"Total"}}""")]
    [InlineData("""{"$schema":"http://powerbi.com/product/schema#advanced","target":{"table":"Sales","measure":"Total"},"filterType":0,"logicalOperator":"And","conditions":[]}""")]
    public void FiltersThatSelectNothing_AreNoOps(string filter)
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"), Samples.Measure("Sales", "Total"));
        r.Filters.Add(Fixtures.Filter(filter));
        var plan = Build(r);
        var q = plan.BuildQuery();
        Assert.DoesNotContain("VAR _f", q);
        Assert.DoesNotContain("VAR _tn", q);
        Assert.DoesNotContain("VAR _vf", q);
        Assert.Empty(plan.Warnings);
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
    public void MeasureFilterWithAnUnsupportedCondition_FailsTheExport()
    {
        var r = Samples.Request(Samples.Column("Sales", "Region"), Samples.Measure("Sales", "Total"));
        r.Filters.Add(Fixtures.Filter(Advanced("""{"table":"Sales","measure":"Total"}""", "And",
            """[{"operator":"GreaterThan","value":1},{"operator":"Between","value":2}]""")));
        var ex = Assert.Throws<ExportRequestException>(() => Build(r));
        Assert.Contains("[Total]", ex.Message);
        Assert.Contains("the condition 'Between' isn't supported", ex.Message);
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
    public void MultiColumnFilterOnABoundColumn_FailsTheExport()
    {
        var targets = """[{"table":"RegionPicker","column":"RegionChoice"},{"table":"CategoryPicker","column":"CategoryChoice"}]""";
        var ex = Assert.Throws<ExportRequestException>(() => BuildDq(Tuple("In", """[["West","Bikes"]]""", targets)));
        Assert.Contains("'RegionPicker'[RegionChoice], which is bound to a dynamic M query parameter", ex.Message);
        Assert.Contains("bound to a dynamic M query parameter",
            Assert.Throws<ExportRequestException>(() => BuildDq(Hierarchy("""[{"operator":"Selected","value":"West"}]""", targets))).Message);
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
