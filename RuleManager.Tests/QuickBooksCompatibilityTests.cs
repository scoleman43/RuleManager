using Microsoft.EntityFrameworkCore;
using NPOI.HSSF.UserModel;
using RuleManager.Core.Domain;
using RuleManager.Data;
using RuleManager.Data.Services;

namespace RuleManager.Tests;

public sealed class QuickBooksCompatibilityTests
{
    [Fact]
    public async Task Import_ParsesVerifiedReusableRuleMappings_AndPreservesRawJson()
    {
        var organizationId = Guid.NewGuid();
        var factory = CreateFactory(nameof(Import_ParsesVerifiedReusableRuleMappings_AndPreservesRawJson));
        await SeedOrganizationAsync(factory, organizationId);

        const string conditions = "{\"ruleConditions\":[{\"ruleType\":10,\"value\":\"-1\"},{\"ruleType\":1,\"value\":\"Sunoco\"}],\"isAndRule\":true}";
        const string outputs = "{\"ruleActions\":[{\"actionType\":0,\"value\":\"Auto:gas/ tolls\"},{\"actionType\":8,\"value\":true}]}";

        using var workbook = BuildWorkbook(("Sunoco", conditions, outputs));
        var service = new QuickBooksRuleImportService(factory);

        var items = await service.AnalyzeAsync(organizationId, workbook);

        var item = Assert.Single(items);
        Assert.Equal(RuleImportStatus.New, item.Status);
        Assert.Equal(RuleDirection.MoneyOut, item.Direction);
        Assert.Equal(RuleTransactionType.Expense, item.TransactionType);
        Assert.True(item.MatchAllConditions);
        Assert.True(item.AutoAdd);
        Assert.Equal("Auto:gas/ tolls", item.CategoryName);
        Assert.Equal(conditions, item.OriginalConditionsJson);
        Assert.Equal(outputs, item.OriginalOutputsJson);

        var condition = Assert.Single(item.Conditions);
        Assert.Equal(RuleMatchField.Description, condition.Field);
        Assert.Equal(RuleMatchOperator.Contains, condition.Operator);
        Assert.Equal("Sunoco", condition.Value);
    }

    [Fact]
    public async Task Import_ParsesBankText_AnyCondition_Transfer_AndAutoAddOff()
    {
        var organizationId = Guid.NewGuid();
        var factory = CreateFactory(nameof(Import_ParsesBankText_AnyCondition_Transfer_AndAutoAddOff));
        await SeedOrganizationAsync(factory, organizationId);

        const string conditions = "{\"ruleConditions\":[{\"ruleType\":6,\"value\":\"ACH TRACE\"},{\"ruleType\":10,\"value\":\"1\"}],\"isAndRule\":false}";
        const string outputs = "{\"ruleActions\":[{\"actionType\":0,\"value\":\"Transfers\"},{\"actionType\":7,\"value\":\"26\"}]}";

        using var workbook = BuildWorkbook(("Incoming transfer", conditions, outputs));
        var service = new QuickBooksRuleImportService(factory);

        var item = Assert.Single(await service.AnalyzeAsync(organizationId, workbook));

        Assert.Equal(RuleDirection.MoneyIn, item.Direction);
        Assert.Equal(RuleTransactionType.Transfer, item.TransactionType);
        Assert.False(item.MatchAllConditions);
        Assert.False(item.AutoAdd);
        var condition = Assert.Single(item.Conditions);
        Assert.Equal(RuleMatchField.BankText, condition.Field);
        Assert.Equal("ACH TRACE", condition.Value);
    }

    [Fact]
    public async Task Import_ParsesVerifiedBankTextExactMatch()
    {
        var organizationId = Guid.NewGuid();
        var factory = CreateFactory(nameof(Import_ParsesVerifiedBankTextExactMatch));
        await SeedOrganizationAsync(factory, organizationId);

        const string conditions = "{\"ruleConditions\":[{\"ruleType\":10,\"value\":\"-1\"},{\"ruleType\":13,\"value\":\"BANK_IS_EXACTLY\"}],\"isAndRule\":true}";
        const string outputs = "{\"ruleActions\":[{\"actionType\":0,\"value\":\"Testing\"}]}";

        using var workbook = BuildWorkbook(("Exact bank text", conditions, outputs));
        var service = new QuickBooksRuleImportService(factory);

        var item = Assert.Single(await service.AnalyzeAsync(organizationId, workbook));
        var condition = Assert.Single(item.Conditions);

        Assert.Equal(RuleImportStatus.New, item.Status);
        Assert.Equal(RuleMatchField.BankText, condition.Field);
        Assert.Equal(RuleMatchOperator.Equals, condition.Operator);
        Assert.Equal("BANK_IS_EXACTLY", condition.Value);
    }

    [Fact]
    public async Task Import_MarksMatchingInactiveReusableRule_AsChangedSoItCanBeReactivated()
    {
        var organizationId = Guid.NewGuid();
        var factory = CreateFactory(nameof(Import_MarksMatchingInactiveReusableRule_AsChangedSoItCanBeReactivated));
        await SeedOrganizationAsync(factory, organizationId);

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.MasterRules.Add(new MasterRule
            {
                OrganizationId = organizationId,
                Name = "(Suggested) Shell as Auto-gas- tolls",
                Direction = RuleDirection.MoneyOut,
                TransactionType = RuleTransactionType.Expense,
                CategoryName = "Auto:gas/ tolls",
                IsActive = false,
                Conditions = new()
                {
                    new RuleCondition
                    {
                        Field = RuleMatchField.Description,
                        Operator = RuleMatchOperator.Contains,
                        Value = "Shell"
                    }
                }
            });
            await db.SaveChangesAsync();
        }

        const string conditions = "{\"ruleConditions\":[{\"ruleType\":10,\"value\":\"-1\"},{\"ruleType\":1,\"value\":\"Shell\"}],\"isAndRule\":true}";
        const string outputs = "{\"ruleActions\":[{\"actionType\":0,\"value\":\"Auto:gas/ tolls\"}]}";

        using var workbook = BuildWorkbook(("(Suggested) Shell as Auto-gas- tolls", conditions, outputs));
        var service = new QuickBooksRuleImportService(factory);

        var item = Assert.Single(await service.AnalyzeAsync(organizationId, workbook));

        Assert.Equal(RuleImportStatus.Changed, item.Status);
        Assert.True(item.Selected);

        await service.ApplyAsync(organizationId, new[] { item });

        await using var verify = await factory.CreateDbContextAsync();
        var rule = await verify.MasterRules.SingleAsync(x => x.Name == "(Suggested) Shell as Auto-gas- tolls");
        Assert.True(rule.IsActive);
    }

    [Fact]
    public async Task Import_ClassifiesCreditCardPayment_AsClientSpecific()
    {
        var organizationId = Guid.NewGuid();
        var factory = CreateFactory(nameof(Import_ClassifiesCreditCardPayment_AsClientSpecific));
        await SeedOrganizationAsync(factory, organizationId);

        const string conditions = "{\"ruleConditions\":[{\"ruleType\":10,\"value\":\"-1\"},{\"ruleType\":1,\"value\":\"AMEX PAYMENT\"}],\"isAndRule\":true}";
        const string outputs = "{\"ruleActions\":[{\"actionType\":0,\"value\":\"American Express 1234\"},{\"actionType\":7,\"value\":\"64\"}]}";

        using var workbook = BuildWorkbook(("AMEX Payment", conditions, outputs));
        var service = new QuickBooksRuleImportService(factory);

        var item = Assert.Single(await service.AnalyzeAsync(organizationId, workbook));

        Assert.Equal(RuleImportStatus.ClientSpecific, item.Status);
        Assert.Equal(RuleTransactionType.CreditCardPayment, item.TransactionType);
        Assert.True(item.IsAccountSpecific);
        Assert.True(item.IsReadOnlyImport);
        Assert.Equal(conditions, item.OriginalConditionsJson);
        Assert.Equal(outputs, item.OriginalOutputsJson);
    }

    [Fact]
    public async Task Import_ParsesVerifiedBankTextAmountAndPayeeMappings()
    {
        var organizationId = Guid.NewGuid();
        var factory = CreateFactory(nameof(Import_ParsesVerifiedBankTextAmountAndPayeeMappings));
        await SeedOrganizationAsync(factory, organizationId);

        using var workbook = BuildWorkbook(
            ("Does not contain",
                "{\"ruleConditions\":[{\"ruleType\":10,\"value\":\"-1\"},{\"ruleType\":8,\"value\":\"BLOCK\"}],\"isAndRule\":true}",
                "{\"ruleActions\":[{\"actionType\":0,\"value\":\"Testing\"}]}"),
            ("Amount equals",
                "{\"ruleConditions\":[{\"ruleType\":10,\"value\":\"-1\"},{\"ruleType\":2,\"value\":\"-100.00\"}],\"isAndRule\":true}",
                "{\"ruleActions\":[{\"actionType\":0,\"value\":\"Testing\"}]}"),
            ("Amount not equal",
                "{\"ruleConditions\":[{\"ruleType\":10,\"value\":\"-1\"},{\"ruleType\":7,\"value\":\"-101.00\"}],\"isAndRule\":true}",
                "{\"ruleActions\":[{\"actionType\":0,\"value\":\"Testing\"}]}"),
            ("Amount greater",
                "{\"ruleConditions\":[{\"ruleType\":10,\"value\":\"-1\"},{\"ruleType\":3,\"value\":\"-102.00\"}],\"isAndRule\":true}",
                "{\"ruleActions\":[{\"actionType\":0,\"value\":\"Testing\"}]}"),
            ("Amount less",
                "{\"ruleConditions\":[{\"ruleType\":10,\"value\":\"-1\"},{\"ruleType\":4,\"value\":\"-103.00\"}],\"isAndRule\":true}",
                "{\"ruleActions\":[{\"actionType\":0,\"value\":\"Testing\"}]}"),
            ("Payee",
                "{\"ruleConditions\":[{\"ruleType\":10,\"value\":\"-1\"},{\"ruleType\":6,\"value\":\"PAYEE\"}],\"isAndRule\":true}",
                "{\"ruleActions\":[{\"actionType\":0,\"value\":\"Office Supplies\"},{\"actionType\":5,\"value\":\"TEST Vendor\"}]}"));

        var service = new QuickBooksRuleImportService(factory);
        var items = await service.AnalyzeAsync(organizationId, workbook);

        Assert.All(items, item => Assert.Equal(RuleImportStatus.New, item.Status));

        Assert.Equal(RuleMatchOperator.DoesNotContain, items[0].Conditions.Single().Operator);

        Assert.Equal(RuleMatchOperator.Equals, items[1].Conditions.Single().Operator);
        Assert.Equal("100.00", items[1].Conditions.Single().Value);

        Assert.Equal(RuleMatchOperator.DoesNotEqual, items[2].Conditions.Single().Operator);
        Assert.Equal("101.00", items[2].Conditions.Single().Value);

        Assert.Equal(RuleMatchOperator.GreaterThan, items[3].Conditions.Single().Operator);
        Assert.Equal("102.00", items[3].Conditions.Single().Value);

        Assert.Equal(RuleMatchOperator.LessThan, items[4].Conditions.Single().Operator);
        Assert.Equal("103.00", items[4].Conditions.Single().Value);

        Assert.Equal("TEST Vendor", items[5].Payee);
    }

    [Fact]
    public async Task Export_GeneratesVerifiedBankTextAmountAndPayeeJson()
    {
        var factory = CreateFactory(nameof(Export_GeneratesVerifiedBankTextAmountAndPayeeJson));
        var (organizationId, clientId) = await SeedClientAsync(factory);

        await using (var db = await factory.CreateDbContextAsync())
        {
            var rule = new MasterRule
            {
                OrganizationId = organizationId,
                Name = "Verified mappings",
                Direction = RuleDirection.MoneyOut,
                TransactionType = RuleTransactionType.Expense,
                CategoryName = "Office Supplies",
                Payee = "TEST Vendor",
                MatchAllConditions = true,
                Conditions = new()
                {
                    new RuleCondition
                    {
                        Field = RuleMatchField.BankText,
                        Operator = RuleMatchOperator.DoesNotContain,
                        Value = "BLOCK"
                    },
                    new RuleCondition
                    {
                        Field = RuleMatchField.Amount,
                        Operator = RuleMatchOperator.GreaterThan,
                        Value = "102"
                    }
                }
            };

            db.MasterRules.Add(rule);
            db.ClientRuleAssignments.Add(new ClientRuleAssignment
            {
                ClientId = clientId,
                MasterRuleId = rule.Id,
                IsExplicit = true,
                ExportPriority = 1
            });
            await db.SaveChangesAsync();
        }

        var service = new QuickBooksRuleExportService(factory);
        var result = await service.GenerateClientExportAsync(clientId);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        using var stream = new MemoryStream(result.Content!);
        using var workbook = new HSSFWorkbook(stream);
        var row = workbook.GetSheetAt(0).GetRow(1);

        Assert.Equal(
            "{\"ruleConditions\":[{\"ruleType\":10,\"value\":\"-1\"},{\"ruleType\":8,\"value\":\"BLOCK\"},{\"ruleType\":3,\"value\":\"-102.00\"}],\"isAndRule\":true}",
            row.GetCell(1).StringCellValue);

        Assert.Equal(
            "{\"ruleActions\":[{\"actionType\":0,\"value\":\"Office Supplies\"},{\"actionType\":5,\"value\":\"TEST Vendor\"}]}",
            row.GetCell(2).StringCellValue);
    }

    [Fact]
    public async Task Import_ParsesFourAndFiveConditionRulesWithoutTruncation()
    {
        var organizationId = Guid.NewGuid();
        var factory = CreateFactory(nameof(Import_ParsesFourAndFiveConditionRulesWithoutTruncation));
        await SeedOrganizationAsync(factory, organizationId);

        const string outputs = "{\"ruleActions\":[{\"actionType\":0,\"value\":\"Testing\"}]}";

        using var workbook = BuildWorkbook(
            ("Four conditions",
                "{\"ruleConditions\":[{\"ruleType\":10,\"value\":\"-1\"},{\"ruleType\":6,\"value\":\"FOUR_A\"},{\"ruleType\":8,\"value\":\"FOUR_B\"},{\"ruleType\":3,\"value\":\"-10.00\"},{\"ruleType\":4,\"value\":\"-999.00\"}],\"isAndRule\":true}",
                outputs),
            ("Five conditions",
                "{\"ruleConditions\":[{\"ruleType\":10,\"value\":\"-1\"},{\"ruleType\":6,\"value\":\"FIVE_A\"},{\"ruleType\":8,\"value\":\"FIVE_B\"},{\"ruleType\":3,\"value\":\"-10.00\"},{\"ruleType\":8,\"value\":\"FIVE_D\"},{\"ruleType\":4,\"value\":\"-999.00\"}],\"isAndRule\":true}",
                outputs));

        var service = new QuickBooksRuleImportService(factory);
        var items = await service.AnalyzeAsync(organizationId, workbook);

        Assert.Equal(2, items.Count);
        Assert.Equal(4, items[0].Conditions.Count);
        Assert.Equal(5, items[1].Conditions.Count);
        Assert.All(items, item => Assert.Equal(RuleImportStatus.New, item.Status));
    }

    [Fact]
    public async Task Import_PreservesVerifiedPercentageAndAmountSplitRules_ForLosslessExport()
    {
        var organizationId = Guid.NewGuid();
        var factory = CreateFactory(nameof(Import_PreservesVerifiedPercentageAndAmountSplitRules_ForLosslessExport));
        await SeedOrganizationAsync(factory, organizationId);

        const string percentageConditions = "{\"ruleConditions\":[{\"ruleType\":10,\"value\":\"-1\"},{\"ruleType\":6,\"value\":\"T033\"}],\"isAndRule\":true}";
        const string percentageOutputs = "{\"ruleActions\":[{\"actionType\":6,\"value\":{\"actionInfoList\":[{\"categoryId\":\"Testing A\",\"splitValue\":\"50\",\"splitType\":\"percentage\"},{\"categoryId\":\"Testing B\",\"splitValue\":\"50\",\"splitType\":\"percentage\"}]}}]}";

        const string amountConditions = "{\"ruleConditions\":[{\"ruleType\":10,\"value\":\"-1\"},{\"ruleType\":6,\"value\":\"T034\"}],\"isAndRule\":true}";
        const string amountOutputs = "{\"ruleActions\":[{\"actionType\":6,\"value\":{\"actionInfoList\":[{\"categoryId\":\"Testing A\",\"splitValue\":\"25\",\"splitType\":\"amount\"},{\"categoryId\":\"Testing B\",\"splitValue\":\"R\",\"splitType\":\"amount\"}]}}]}";

        using var workbook = BuildWorkbook(
            ("T033", percentageConditions, percentageOutputs),
            ("T034", amountConditions, amountOutputs));

        var importService = new QuickBooksRuleImportService(factory);
        var items = await importService.AnalyzeAsync(organizationId, workbook);

        Assert.Equal(2, items.Count);
        Assert.All(items, item =>
        {
            Assert.Equal(RuleImportStatus.New, item.Status);
            Assert.True(item.IsSplitRule);
            Assert.True(item.IsReadOnlyImport);
            Assert.Null(item.UnsupportedReason);
            Assert.True(item.Selected);
        });

        await importService.ApplyAsync(organizationId, items);

        await using var db = await factory.CreateDbContextAsync();
        var rules = await db.MasterRules.OrderBy(x => x.Name).ToListAsync();

        Assert.Equal(2, rules.Count);
        Assert.All(rules, rule => Assert.True(rule.IsReadOnlyImport));
        Assert.Equal(percentageOutputs, rules[0].OriginalOutputsJson);
        Assert.Equal(amountOutputs, rules[1].OriginalOutputsJson);
    }

    [Fact]
    public async Task Export_PreservesImportedSplitRuleJsonExactly()
    {
        var factory = CreateFactory(nameof(Export_PreservesImportedSplitRuleJsonExactly));
        var (organizationId, clientId) = await SeedClientAsync(factory);

        const string conditions = "{\"ruleConditions\":[{\"ruleType\":10,\"value\":\"-1\"},{\"ruleType\":6,\"value\":\"T033\"}],\"isAndRule\":true}";
        const string outputs = "{\"ruleActions\":[{\"actionType\":6,\"value\":{\"actionInfoList\":[{\"categoryId\":\"Testing A\",\"splitValue\":\"50\",\"splitType\":\"percentage\"},{\"categoryId\":\"Testing B\",\"splitValue\":\"50\",\"splitType\":\"percentage\"}]}}]}";

        await using (var db = await factory.CreateDbContextAsync())
        {
            var rule = new MasterRule
            {
                OrganizationId = organizationId,
                Name = "T033",
                Direction = RuleDirection.MoneyOut,
                TransactionType = RuleTransactionType.Expense,
                Conditions = new()
                {
                    new RuleCondition
                    {
                        Field = RuleMatchField.BankText,
                        Operator = RuleMatchOperator.Contains,
                        Value = "T033"
                    }
                },
                IsReadOnlyImport = true,
                OriginalConditionsJson = conditions,
                OriginalOutputsJson = outputs
            };

            db.MasterRules.Add(rule);
            db.ClientRuleAssignments.Add(new ClientRuleAssignment
            {
                ClientId = clientId,
                MasterRuleId = rule.Id,
                IsExplicit = true,
                ExportPriority = 1
            });
            await db.SaveChangesAsync();
        }

        var exportService = new QuickBooksRuleExportService(factory);
        var result = await exportService.GenerateClientExportAsync(clientId);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        using var stream = new MemoryStream(result.Content!);
        using var workbook = new HSSFWorkbook(stream);
        var row = workbook.GetSheetAt(0).GetRow(1);

        Assert.Equal("T033", row.GetCell(0).StringCellValue);
        Assert.Equal(conditions, row.GetCell(1).StringCellValue);
        Assert.Equal(outputs, row.GetCell(2).StringCellValue);
    }

    [Fact]
    public void SplitCodec_ParsesAndSerializesVerifiedPercentageAndAmountFormats()
    {
        const string percentageOutputs = "{\"ruleActions\":[{\"actionType\":6,\"value\":{\"actionInfoList\":[{\"categoryId\":\"Testing A\",\"splitValue\":\"50\",\"splitType\":\"percentage\"},{\"categoryId\":\"Testing B\",\"splitValue\":\"50\",\"splitType\":\"percentage\"}]}}]}";
        const string amountOutputs = "{\"ruleActions\":[{\"actionType\":6,\"value\":{\"actionInfoList\":[{\"categoryId\":\"Testing A\",\"splitValue\":\"25\",\"splitType\":\"amount\"},{\"categoryId\":\"Testing B\",\"splitValue\":\"R\",\"splitType\":\"amount\"}]}}]}";

        Assert.True(QuickBooksSplitRuleCodec.TryParse(percentageOutputs, out var percentage));
        Assert.NotNull(percentage);
        Assert.Equal(QuickBooksSplitType.Percentage, percentage!.Type);
        Assert.Equal(2, percentage.Lines.Count);
        Assert.Null(QuickBooksSplitRuleCodec.Validate(percentage));
        Assert.Equal(percentageOutputs, QuickBooksSplitRuleCodec.Serialize(percentage));

        Assert.True(QuickBooksSplitRuleCodec.TryParse(amountOutputs, out var amount));
        Assert.NotNull(amount);
        Assert.Equal(QuickBooksSplitType.Amount, amount!.Type);
        Assert.Equal(2, amount.Lines.Count);
        Assert.True(amount.Lines[1].IsRemainder);
        Assert.Null(QuickBooksSplitRuleCodec.Validate(amount));
        Assert.Equal(amountOutputs, QuickBooksSplitRuleCodec.Serialize(amount));
    }

    [Fact]
    public async Task Export_GeneratesEditableSplitRuleJson_FromCurrentConditionsAndSplitDefinition()
    {
        var factory = CreateFactory(nameof(Export_GeneratesEditableSplitRuleJson_FromCurrentConditionsAndSplitDefinition));
        var (organizationId, clientId) = await SeedClientAsync(factory);

        var splitDefinition = new QuickBooksSplitDefinition
        {
            Type = QuickBooksSplitType.Percentage,
            Lines = new()
            {
                new QuickBooksSplitLine { CategoryName = "Testing A", Value = "60" },
                new QuickBooksSplitLine { CategoryName = "Testing B", Value = "40" }
            }
        };

        await using (var db = await factory.CreateDbContextAsync())
        {
            var rule = new MasterRule
            {
                OrganizationId = organizationId,
                Name = "Editable split",
                Direction = RuleDirection.MoneyOut,
                TransactionType = RuleTransactionType.Expense,
                MatchAllConditions = true,
                IsReadOnlyImport = false,
                OriginalOutputsJson = QuickBooksSplitRuleCodec.Serialize(splitDefinition),
                Conditions = new()
                {
                    new RuleCondition
                    {
                        Field = RuleMatchField.BankText,
                        Operator = RuleMatchOperator.Contains,
                        Value = "SPLIT TEST"
                    }
                }
            };

            db.MasterRules.Add(rule);
            db.ClientRuleAssignments.Add(new ClientRuleAssignment
            {
                ClientId = clientId,
                MasterRuleId = rule.Id,
                IsExplicit = true,
                ExportPriority = 1
            });
            await db.SaveChangesAsync();
        }

        var exportService = new QuickBooksRuleExportService(factory);
        var result = await exportService.GenerateClientExportAsync(clientId);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        using var stream = new MemoryStream(result.Content!);
        using var workbook = new HSSFWorkbook(stream);
        var row = workbook.GetSheetAt(0).GetRow(1);

        Assert.Equal(
            "{\"ruleConditions\":[{\"ruleType\":10,\"value\":\"-1\"},{\"ruleType\":6,\"value\":\"SPLIT TEST\"}],\"isAndRule\":true}",
            row.GetCell(1).StringCellValue);
        Assert.Equal(
            "{\"ruleActions\":[{\"actionType\":6,\"value\":{\"actionInfoList\":[{\"categoryId\":\"Testing A\",\"splitValue\":\"60\",\"splitType\":\"percentage\"},{\"categoryId\":\"Testing B\",\"splitValue\":\"40\",\"splitType\":\"percentage\"}]}}]}",
            row.GetCell(2).StringCellValue);
    }

    [Fact]
    public async Task Export_WritesRulesInClientPriorityOrder()
    {
        var factory = CreateFactory(nameof(Export_WritesRulesInClientPriorityOrder));
        var (organizationId, clientId) = await SeedClientAsync(factory);

        await using (var db = await factory.CreateDbContextAsync())
        {
            var first = ReusableRule(organizationId, "First", "ONE");
            var second = ReusableRule(organizationId, "Second", "TWO");
            var third = ReusableRule(organizationId, "Third", "THREE");
            db.MasterRules.AddRange(first, second, third);
            db.ClientRuleAssignments.AddRange(
                new ClientRuleAssignment { ClientId = clientId, MasterRuleId = second.Id, IsExplicit = true, ExportPriority = 2 },
                new ClientRuleAssignment { ClientId = clientId, MasterRuleId = third.Id, IsExplicit = true, ExportPriority = 3 },
                new ClientRuleAssignment { ClientId = clientId, MasterRuleId = first.Id, IsExplicit = true, ExportPriority = 1 });
            await db.SaveChangesAsync();
        }

        var service = new QuickBooksRuleExportService(factory);
        var result = await service.GenerateClientExportAsync(clientId);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.NotNull(result.Content);

        using var stream = new MemoryStream(result.Content!);
        var names = ReadRuleNames(stream);
        Assert.Equal(new[] { "First", "Second", "Third" }, names);
    }

    [Fact]
    public async Task Export_PreservesClientSpecificQuickBooksJsonExactly()
    {
        var factory = CreateFactory(nameof(Export_PreservesClientSpecificQuickBooksJsonExactly));
        var (_, clientId) = await SeedClientAsync(factory);

        const string conditions = "{\"ruleConditions\":[{\"ruleType\":10,\"value\":\"-1\"},{\"ruleType\":1,\"value\":\"CARD PAY\"}],\"isAndRule\":true}";
        const string outputs = "{\"ruleActions\":[{\"actionType\":0,\"value\":\"Visa 4321\"},{\"actionType\":7,\"value\":\"64\"},{\"actionType\":8,\"value\":true}]}";

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ClientRules.Add(new ClientRule
            {
                ClientId = clientId,
                Name = "Card Payment",
                Direction = RuleDirection.MoneyOut,
                TransactionType = RuleTransactionType.CreditCardPayment,
                CategoryName = "Visa 4321",
                Conditions = new()
                {
                    new RuleCondition
                    {
                        Field = RuleMatchField.Description,
                        Operator = RuleMatchOperator.Contains,
                        Value = "CARD PAY"
                    }
                },
                AutoAdd = true,
                IsAccountSpecific = true,
                IsReadOnlyImport = true,
                OriginalConditionsJson = conditions,
                OriginalOutputsJson = outputs,
                ExportPriority = 1
            });
            await db.SaveChangesAsync();
        }

        var service = new QuickBooksRuleExportService(factory);
        var result = await service.GenerateClientExportAsync(clientId);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        using var stream = new MemoryStream(result.Content!);
        using var workbook = new HSSFWorkbook(stream);
        var row = workbook.GetSheetAt(0).GetRow(1);

        Assert.Equal("Card Payment", row.GetCell(0).StringCellValue);
        Assert.Equal(conditions, row.GetCell(1).StringCellValue);
        Assert.Equal(outputs, row.GetCell(2).StringCellValue);
    }

    [Fact]
    public async Task Export_GeneratesVerifiedBankTextExactMatchJson()
    {
        var factory = CreateFactory(nameof(Export_GeneratesVerifiedBankTextExactMatchJson));
        var (organizationId, clientId) = await SeedClientAsync(factory);

        await using (var db = await factory.CreateDbContextAsync())
        {
            var rule = new MasterRule
            {
                OrganizationId = organizationId,
                Name = "Exact bank text",
                Direction = RuleDirection.MoneyOut,
                TransactionType = RuleTransactionType.Expense,
                CategoryName = "Testing",
                Conditions = new()
                {
                    new RuleCondition
                    {
                        Field = RuleMatchField.BankText,
                        Operator = RuleMatchOperator.Equals,
                        Value = "BANK_IS_EXACTLY"
                    }
                }
            };

            db.MasterRules.Add(rule);
            db.ClientRuleAssignments.Add(new ClientRuleAssignment
            {
                ClientId = clientId,
                MasterRuleId = rule.Id,
                IsExplicit = true,
                ExportPriority = 1
            });
            await db.SaveChangesAsync();
        }

        var service = new QuickBooksRuleExportService(factory);
        var result = await service.GenerateClientExportAsync(clientId);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        using var stream = new MemoryStream(result.Content!);
        using var workbook = new HSSFWorkbook(stream);
        var row = workbook.GetSheetAt(0).GetRow(1);

        Assert.Equal(
            "{\"ruleConditions\":[{\"ruleType\":10,\"value\":\"-1\"},{\"ruleType\":13,\"value\":\"BANK_IS_EXACTLY\"}],\"isAndRule\":true}",
            row.GetCell(1).StringCellValue);
    }

    [Fact]
    public async Task Export_GeneratesVerifiedReusableJson_ForDescriptionCheckAndAutoAdd()
    {
        var factory = CreateFactory(nameof(Export_GeneratesVerifiedReusableJson_ForDescriptionCheckAndAutoAdd));
        var (organizationId, clientId) = await SeedClientAsync(factory);

        await using (var db = await factory.CreateDbContextAsync())
        {
            var rule = new MasterRule
            {
                OrganizationId = organizationId,
                Name = "Check Rule",
                Direction = RuleDirection.MoneyOut,
                TransactionType = RuleTransactionType.Check,
                CategoryName = "Office Supplies",
                AutoAdd = true,
                MatchAllConditions = true,
                Conditions = new()
                {
                    new RuleCondition
                    {
                        Field = RuleMatchField.Description,
                        Operator = RuleMatchOperator.Contains,
                        Value = "STAPLES"
                    }
                }
            };
            db.MasterRules.Add(rule);
            db.ClientRuleAssignments.Add(new ClientRuleAssignment
            {
                ClientId = clientId,
                MasterRuleId = rule.Id,
                IsExplicit = true,
                ExportPriority = 1
            });
            await db.SaveChangesAsync();
        }

        var service = new QuickBooksRuleExportService(factory);
        var result = await service.GenerateClientExportAsync(clientId);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        using var stream = new MemoryStream(result.Content!);
        using var workbook = new HSSFWorkbook(stream);
        var row = workbook.GetSheetAt(0).GetRow(1);

        Assert.Equal(
            "{\"ruleConditions\":[{\"ruleType\":10,\"value\":\"-1\"},{\"ruleType\":1,\"value\":\"STAPLES\"}],\"isAndRule\":true}",
            row.GetCell(1).StringCellValue);

        Assert.Equal(
            "{\"ruleActions\":[{\"actionType\":0,\"value\":\"Office Supplies\"},{\"actionType\":7,\"value\":\"3\"},{\"actionType\":8,\"value\":true}]}",
            row.GetCell(2).StringCellValue);
    }

    private static TestDbContextFactory CreateFactory(string databaseName) =>
        new(databaseName);

    private static async Task SeedOrganizationAsync(TestDbContextFactory factory, Guid organizationId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.Organizations.Add(new Organization { Id = organizationId, Name = "Test Firm" });
        await db.SaveChangesAsync();
    }

    private static async Task<(Guid OrganizationId, Guid ClientId)> SeedClientAsync(TestDbContextFactory factory)
    {
        var organizationId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        await using var db = await factory.CreateDbContextAsync();
        db.Organizations.Add(new Organization { Id = organizationId, Name = "Test Firm" });
        db.Clients.Add(new Client
        {
            Id = clientId,
            OrganizationId = organizationId,
            Name = "Compatibility Test Client"
        });
        await db.SaveChangesAsync();
        return (organizationId, clientId);
    }

    private static MasterRule ReusableRule(Guid organizationId, string name, string match) =>
        new()
        {
            OrganizationId = organizationId,
            Name = name,
            Direction = RuleDirection.MoneyOut,
            TransactionType = RuleTransactionType.Expense,
            CategoryName = "Testing",
            Conditions = new()
            {
                new RuleCondition
                {
                    Field = RuleMatchField.Description,
                    Operator = RuleMatchOperator.Contains,
                    Value = match
                }
            }
        };

    private static MemoryStream BuildWorkbook(params (string Name, string Conditions, string Outputs)[] rules)
    {
        using var workbook = new HSSFWorkbook();
        var sheet = workbook.CreateSheet("Rules");
        var header = sheet.CreateRow(0);
        header.CreateCell(0).SetCellValue("Rule Name");
        header.CreateCell(1).SetCellValue("Rule Conditions");
        header.CreateCell(2).SetCellValue("Rule Outputs");

        for (var i = 0; i < rules.Length; i++)
        {
            var row = sheet.CreateRow(i + 1);
            row.CreateCell(0).SetCellValue(rules[i].Name);
            row.CreateCell(1).SetCellValue(rules[i].Conditions);
            row.CreateCell(2).SetCellValue(rules[i].Outputs);
        }

        var stream = new MemoryStream();
        workbook.Write(stream);
        stream.Position = 0;
        return stream;
    }

    private static IReadOnlyList<string> ReadRuleNames(Stream stream)
    {
        using var workbook = new HSSFWorkbook(stream);
        var sheet = workbook.GetSheetAt(0);
        var names = new List<string>();

        for (var i = 1; i <= sheet.LastRowNum; i++)
            names.Add(sheet.GetRow(i).GetCell(0).StringCellValue);

        return names;
    }

    private sealed class TestDbContextFactory(string databaseName) : IDbContextFactory<RuleManagerDbContext>
    {
        private readonly DbContextOptions<RuleManagerDbContext> options =
            new DbContextOptionsBuilder<RuleManagerDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options;

        public RuleManagerDbContext CreateDbContext() => new(options);

        public Task<RuleManagerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
