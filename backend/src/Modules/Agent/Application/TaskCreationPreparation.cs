using System.Text.Json;
using PersonalDashboard.V2.Agent.Domain;
using PersonalDashboard.V2.Contracts.AgentAccess;
using PersonalDashboard.V2.Contracts.Chat;

namespace PersonalDashboard.V2.Agent.Application;

internal static class TaskCreationPreparation
{
    public static readonly ModelTool Tool = new("prepare_task",
        "Подготовить одну новую задачу для кнопки подтверждения. Отдели короткое название и только предназначенное для описания содержание от слов управления; если пользователь не диктовал содержание, description пустой. " +
        "По умолчанию задача относится к Backlog: выбери подходящий существующий раздел по смыслу, а если ни один не подходит — существующий «Общее». Разделы не создавай. Если «Общее» отсутствует и раздел не подходит, спроси, куда поместить задачу. " +
        "Если пользователь явно называет существующую фичу, выбери её полный путь и destination planned. Если пользователь просит сразу в Backlog, выбери destination backlog и приложи destination_quote — точную цитату. feature_quote также должна быть точной цитатой пользователя, указывающей фичу. Проект или этап без фичи недостаточен: уточни фичу. Не используй текущую страницу как признак назначения. «Туда» можно разрешить только по однозначному недавнему контексту пользователя. " +
        "section — название существующего раздела Backlog; для фичи укажи section null. feature_quote и destination_quote должны цитировать явное указание пользователя. Не копируй всё сообщение в description. При правке черновика сохрани неизменённые поля; если пользователь просит убрать описание или связь с фичей, передай пустое description или feature null. Если нет названия или темы, задай уточняющий вопрос.",
        """
        {"type":"object","properties":{"title":{"type":"string"},"description":{"type":"string","description":"Только содержание задачи, пустая строка если не диктовали. В правке сохрани тело, если пользователь не просит его изменить."},"destination":{"type":"string","enum":["backlog","planned"]},"section":{"type":["string","null"],"description":"Существующий раздел Backlog или null для связанной задачи."},"section_quote":{"type":["string","null"]},"feature":{"type":["string","null"],"description":"Полный путь существующей фичи или null."},"feature_quote":{"type":["string","null"],"description":"Если feature не null, обязательно верни точную цитату пользователя, которая называет выбранную фичу; если feature null, верни null."},"project":{"type":["string","null"],"description":"Если явно назван только проект — укажи его, чтобы запросить фичу."},"milestone":{"type":["string","null"],"description":"Если явно назван только этап — укажи его, чтобы запросить фичу."},"destination_quote":{"type":["string","null"],"description":"Обязательно верни точную цитату явной просьбы сразу поместить связанную задачу в Backlog; иначе null."},"reason":{"type":"string"}},"required":["title","description","destination","section","feature","feature_quote","destination_quote"],"additionalProperties":false}
        """);

    public static async Task<string> ToProposalArgumentsAsync(string arguments, ITasksAgentAccess tasks,
        IPlanningAgentAccess planning, bool catalogLoaded, AgentTurnRequest request, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(arguments);
        var root = doc.RootElement;
        var title = Get(root, "title") ?? throw new InvalidDataException("Укажите название или тему задачи.");
        var previous = request.PendingProposal?.Actions.FirstOrDefault(action => action.EntityType == "tasks.task")?.Payload;
        var description = root.TryGetProperty("description", out var descriptionValue) && descriptionValue.ValueKind == JsonValueKind.String
            ? descriptionValue.GetString()?.Trim() ?? ""
            : PreviousDescription(previous) ?? throw new InvalidDataException("Повтори prepare_task и обязательно передай поле description. Если содержание не диктовали — передай пустую строку; при правке сохрани прежнее описание, если пользователь не просил его изменить.");
        var target = Get(root, "feature");
        var projectOnly = Get(root, "project");
        var milestoneOnly = Get(root, "milestone");
        var featureQuote = Get(root, "feature_quote");
        var destinationQuote = Get(root, "destination_quote");
        var sectionQuote = Get(root, "section_quote");
        var isFeatureExplicit = IsGrounded(featureQuote, request);
        var isDestinationExplicit = IsGrounded(destinationQuote, request);
        var isSectionExplicit = IsGrounded(sectionQuote, request);
        if (!catalogLoaded)
            throw new InvalidDataException("Сначала вызови list_task_destinations. Затем выбери существующий раздел или фичу; не спрашивай необязательные поля.");
        if (target is null && (projectOnly is not null || milestoneOnly is not null))
            throw new InvalidDataException("Для задачи недостаточно проекта или этапа. Уточни, в какую существующую фичу её связать.");

        var priorFeaturePath = previous is { } prior ? Get(prior, "featurePath") : null;
        var priorPlacement = previous is { } oldPlacement ? Get(oldPlacement, "placement") : null;

        IReadOnlyList<TaskFeatureTarget> features = await planning.ListTaskFeaturesAsync(cancellationToken);
        IReadOnlyList<TaskBacklogSection> sections = await tasks.ListBacklogSectionsAsync(cancellationToken);
        TaskFeatureTarget? feature = null;
        if (target is not null)
        {
            var retainedTarget = priorFeaturePath is not null && Same(priorFeaturePath, target);
            var matches = features.Where(item => Same(item.Path, target) || Same(item.FeatureTitle, target)).ToArray();
            if (matches.Length == 0)
                throw new InvalidDataException($"Фича «{target}» не найдена среди активных фич. Уточни её полный путь.");
            if (matches.Length > 1)
                throw new InvalidDataException($"Фича «{target}» неоднозначна. Уточни полный путь: {string.Join("; ", matches.Select(item => item.Path))}.");
            if (!retainedTarget && (!isFeatureExplicit || !FeatureEvidence(featureQuote!, matches[0])))
            {
                var userText = string.Join(" ", request.RecentMessages.Where(message => message.Role == "user").Select(message => message.Content).Append(request.Prompt));
                if (userText.Contains("фич", StringComparison.OrdinalIgnoreCase) || userText.Contains(matches[0].FeatureTitle, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Для уже указанной фичи повтори prepare_task с feature_quote: точной цитатой сообщения пользователя, где названа фича (например, «в фичу авторизации»). Это уточнение аргумента инструмента, не вопрос пользователю.");
                throw new InvalidDataException("Пользователь не указал существующую фичу. Спроси, к какой фиче привязать задачу; не делай выбор за него.");
            }
            var sameNamed = features.Where(item => Same(item.FeatureTitle, matches[0].FeatureTitle)).ToArray();
            if (sameNamed.Length > 1 && !retainedTarget && !NamesDisambiguate(featureQuote!, matches[0], sameNamed))
                throw new InvalidDataException($"Название фичи неоднозначно. Спроси пользователя, какой путь нужен: {string.Join("; ", sameNamed.Select(item => item.Path))}. Не выбирай один из них самостоятельно.");
            feature = matches[0];
        }
        var destination = Get(root, "destination")?.ToLowerInvariant();
        if (feature is not null)
        {
            if (destination is null) destination = "planned";
            if (destination is not ("planned" or "backlog")) throw new InvalidDataException("Для связанной задачи выбери planned или backlog.");
            var retainedBacklogLink = priorFeaturePath is not null && Same(priorFeaturePath, feature.Path) && priorPlacement == "backlog";
            if (destination == "backlog" && !retainedBacklogLink && (!isDestinationExplicit || !MentionsBacklog(destinationQuote!)))
                throw new InvalidDataException("Для переноса связанной задачи в Backlog нужна явная просьба пользователя. Повтори prepare_task с destination_quote, точной цитатой его просьбы; если он этого не говорил, укажи destination planned. Это уточнение аргумента инструмента, не вопрос пользователю.");
        }
        else
        {
            if (destination is not (null or "backlog"))
                throw new InvalidDataException("Размещение в плане требует существующей фичи. Уточни её.");
            destination = "backlog";
        }

        TaskBacklogSection? section = null;
        if (feature is null)
        {
            var sectionName = Get(root, "section");
            if (sectionName is null)
                throw new InvalidDataException("Выбери наиболее подходящий раздел Backlog. Если подходящего нет — существующий раздел «Общее»; если его нет, попроси уточнение.");
            var matches = sections.Where(item => Same(item.Name, sectionName)).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException(matches.Length == 0
                    ? $"Раздел Backlog «{sectionName}» не найден. Выбери существующий раздел; новый создавать нельзя."
                    : $"Раздел «{sectionName}» неоднозначен. Уточни точное название.");
            section = matches[0];
        }

        var placementReason = feature is not null
            ? destination == "planned" ? "Связана с указанной фичей и будет добавлена в план." : "Связана с указанной фичей и будет добавлена в Backlog по просьбе пользователя."
            : isSectionExplicit ? "Пользователь указал этот раздел Backlog." : "Выбран наиболее подходящий существующий раздел Backlog.";
        var after = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["description"] = description,
            ["placement"] = destination,
            ["sectionId"] = section?.Id,
            ["planning"] = feature is null ? null : new { projectId = feature.ProjectId, milestoneId = feature.MilestoneId, featureId = feature.FeatureId },
            ["backlogSectionName"] = section?.Name,
            ["expectedBacklogSectionVersion"] = section?.Version,
            ["featurePath"] = feature?.Path,
            ["expectedProjectVersion"] = feature?.ProjectVersion,
            ["expectedMilestoneVersion"] = feature?.MilestoneVersion,
            ["expectedFeatureVersion"] = feature?.FeatureVersion,
            ["taskPlacementReason"] = placementReason
        };
        return JsonSerializer.Serialize(new { changes = new[] { new { module = "Tasks", operation = "Create", entityType = "tasks.task", after } } });
    }

    public static bool MatchesPending(ChangeProposal proposed, ChatProposal? pending)
    {
        if (pending?.State != ChatProposalState.Pending || pending.Actions.Count != 1 || proposed.Changes.Count != 1) return false;
        var before = pending.Actions[0];
        var after = proposed.Changes[0];
        if (before.EntityType != "tasks.task" || after.Target.EntityType != "tasks.task") return false;
        using var json = JsonDocument.Parse(after.AfterJson);
        return new[] { "title", "description", "placement", "sectionId", "backlogSectionName", "expectedBacklogSectionVersion", "featurePath", "expectedProjectVersion", "expectedMilestoneVersion", "expectedFeatureVersion" }
            .All(field => Value(before.Payload, field) == Value(json.RootElement, field)) &&
            new[] { "projectId", "milestoneId", "featureId" }.All(field => PlanningId(before.Payload, field) == PlanningId(json.RootElement, field));
    }

    private static bool IsGrounded(string? quote, AgentTurnRequest request) => quote is not null &&
        request.RecentMessages.Where(message => message.Role == "user").Select(message => message.Content).Append(request.Prompt)
            .Any(text => text.Contains(quote, StringComparison.OrdinalIgnoreCase));
    private static bool MentionsBacklog(string quote) => quote.Contains("бэклог", StringComparison.OrdinalIgnoreCase)
        || quote.Contains("backlog", StringComparison.OrdinalIgnoreCase);
    private static bool NamesDisambiguate(string quote, TaskFeatureTarget feature, IReadOnlyList<TaskFeatureTarget> sameNamed) =>
        quote.Contains(feature.Path, StringComparison.OrdinalIgnoreCase)
        || (quote.Contains(feature.ProjectTitle, StringComparison.OrdinalIgnoreCase) && sameNamed.Count(candidate => candidate.ProjectTitle == feature.ProjectTitle) == 1)
        || (quote.Contains(feature.MilestoneTitle, StringComparison.OrdinalIgnoreCase) && sameNamed.Count(candidate => candidate.MilestoneTitle == feature.MilestoneTitle) == 1);
    private static bool FeatureEvidence(string quote, TaskFeatureTarget feature) => quote.Contains("фич", StringComparison.OrdinalIgnoreCase)
        || quote.Contains(feature.FeatureTitle, StringComparison.OrdinalIgnoreCase)
        || quote.Contains(feature.Path, StringComparison.OrdinalIgnoreCase);
    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    private static string? Get(JsonElement json, string property) => json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;
    private static string? PreviousDescription(JsonElement? payload) => payload is { } value && value.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String
        ? description.GetString() ?? ""
        : null;
    private static string? Value(JsonElement json, string property) => !json.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null ? null : value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    private static string? PlanningId(JsonElement json, string property) => json.TryGetProperty("planning", out var planning) && planning.ValueKind == JsonValueKind.Object && planning.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
