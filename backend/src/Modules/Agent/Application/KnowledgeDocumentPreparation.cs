using System.Text.Json;
using PersonalDashboard.V2.Contracts.AgentAccess;
using PersonalDashboard.V2.Agent.Domain;
using PersonalDashboard.V2.Contracts.Chat;

namespace PersonalDashboard.V2.Agent.Application;

// The model supplies human-readable values; stable IDs and proposal metadata belong to the server.
internal static class KnowledgeDocumentPreparation
{
    public static readonly ModelTool Tool = new("prepare_knowledge_document",
        "Подготовить один новый документ или исправленный черновик для кнопки «Создать документ». " +
        "Из сообщения отдельно извлеки название, содержание и раздел. Если содержания не было, markdown пустой. " +
        "section — название/полный путь существующего раздела или 'корень'. Если пользователь раздел не указал, " +
        "сначала получи list_knowledge_sections и выбери по смыслу, иначе корень. " +
        "section_quote — точная цитата из сообщения пользователя, где он указывает расположение (например 'в Финансы'), либо null. " +
        "reason — краткая причина выбора. Отсутствие раздела или описания не требует уточнения. " +
        "В markdown убери слова-паразиты, исправь пунктуацию, сохрани факты и сомнения. " +
        "Для правки черновика сохрани остальные поля. Если нет даже темы/названия, задай вопрос вместо вызова.",
        """
        {"type":"object","properties":{"title":{"type":"string","description":"Название документа"},"markdown":{"type":"string","description":"Отредактированное содержание без слов-паразитов и инструкций. Пустая строка, если содержание не диктовали."},"section":{"type":["string","null"],"description":"Полный путь выбранного раздела из каталога либо корень."},"section_quote":{"type":["string","null"],"description":"Точная цитата пользователя, указавшего расположение, иначе null. Не выдумывай цитаты."},"reason":{"type":"string","description":"Краткая причина выбора раздела по смыслу, на языке пользователя."}},"required":["title","markdown","section","section_quote"],"additionalProperties":false}
        """);

    public static async Task<string> ToProposalArgumentsAsync(string arguments, IKnowledgeAgentAccess knowledge,
        bool catalogLoaded, AgentTurnRequest request, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(arguments);
        var input = document.RootElement;
        var title = Value(input, "title") ?? throw new InvalidDataException("Укажите название или тему документа.");
        var markdown = input.TryGetProperty("markdown", out var body) && body.ValueKind == JsonValueKind.String
            ? body.GetString()! : "";
        var selectedPath = Value(input, "section");
        if (IsRoot(selectedPath)) selectedPath = null;
        var previous = request.PendingProposal?.Actions.FirstOrDefault(action => action.EntityType == "knowledge.document")?.Payload;
        var samePlacement = previous is { } old && string.Equals(Value(old, "parentSectionPath"), selectedPath, StringComparison.OrdinalIgnoreCase);
        var quote = Value(input, "section_quote");
        // A model's claim that the user named a section is not evidence: the quote must occur in user text.
        var explicitlyRequested = quote is not null && request.RecentMessages
            .Where(message => message.Role == "user").Select(message => message.Content).Append(request.Prompt)
            .Any(text => text.Contains(quote, StringComparison.OrdinalIgnoreCase));
        var sections = await knowledge.ListSectionsAsync(cancellationToken);
        // A quote of the document request alone does not make an inferred location explicit.
        explicitlyRequested = explicitlyRequested && (sections.Any(candidate =>
            quote!.Contains(candidate.Title, StringComparison.OrdinalIgnoreCase) || quote.Contains(candidate.Path, StringComparison.OrdinalIgnoreCase))
            || quote!.Contains("корень", StringComparison.OrdinalIgnoreCase) || quote.Contains("корне", StringComparison.OrdinalIgnoreCase));
        if (!explicitlyRequested && !catalogLoaded && !samePlacement)
            throw new InvalidDataException("Сначала вызови list_knowledge_sections, затем предложи подходящий раздел или корень. Не спрашивай пользователя, куда положить документ.");
        KnowledgeNodeState? section = null;
        if (selectedPath is not null)
        {
            var matches = sections.Where(candidate => !candidate.Archived &&
                (string.Equals(candidate.Path, selectedPath, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(candidate.Title, selectedPath, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matches.Length == 0)
                throw new InvalidDataException($"Раздел «{selectedPath}» не найден. Уточни название у пользователя; не заменяй его другим разделом.");
            if (matches.Length > 1)
                throw new InvalidDataException($"Раздел «{selectedPath}» неоднозначен. Спроси пользователя, какой полный путь нужен: {string.Join("; ", matches.Select(x => x.Path))}.");
            section = matches[0];
            if (explicitlyRequested && sections.Count(candidate => string.Equals(candidate.Title, section.Title, StringComparison.OrdinalIgnoreCase)) > 1
                && !quote!.Contains(section.Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Название «{section.Title}» встречается в нескольких разделах. Уточни полный путь у пользователя.");
        }
        if (explicitlyRequested)
        {
            var named = sections.Select(candidate => new
            {
                candidate.Id,
                Length = quote!.Contains(candidate.Path, StringComparison.OrdinalIgnoreCase) ? candidate.Path.Length
                    : quote.Contains(candidate.Title, StringComparison.OrdinalIgnoreCase) ? candidate.Title.Length : 0
            }).Where(candidate => candidate.Length > 0).ToArray();
            if (named.Length > 0 && !named.Any(candidate => candidate.Length == named.Max(x => x.Length) && candidate.Id == section?.Id))
                throw new InvalidDataException("Выбранное расположение не соответствует разделу в цитате пользователя. Используй названный им раздел.");
        }
        var kind = explicitlyRequested
            ? section is null ? "root_explicit" : "explicit_section"
            : section is null ? "root_inferred" : "inferred_section";
        if (samePlacement && !explicitlyRequested && previous is { } prior)
            kind = Value(prior, "placementKind") ?? kind;
        var after = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["markdown"] = markdown,
            ["parent_section_id"] = section?.Id,
            ["section_query"] = section?.Path,
            ["placement_kind"] = kind,
            ["placement_reason"] = Value(input, "reason") ?? (section is null
                ? "Среди существующих разделов нет подходящего по теме документа."
                : "тема документа соответствует этому разделу.")
        };
        return JsonSerializer.Serialize(new { changes = new[] { new { module = "Knowledge", operation = "Create", entityType = "knowledge.document", after } } });
    }

    private static string? Value(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;

    private static bool IsRoot(string? value) => value is not null &&
        new[] { "корень", "в корне", "корень базы знаний", "root", "/" }.Contains(value, StringComparer.OrdinalIgnoreCase);

    public static bool MatchesPending(ChangeProposal proposed, ChatProposal? pending)
    {
        if (pending?.State != ChatProposalState.Pending || pending.Actions.Count != 1 || proposed.Changes.Count != 1)
            return false;
        var before = pending.Actions[0];
        var after = proposed.Changes[0];
        if (before.EntityType != "knowledge.document" || after.Target.EntityType != "knowledge.document") return false;
        using var json = JsonDocument.Parse(after.AfterJson);
        return new[] { "title", "markdown", "parentSectionId", "parentSectionPath", "expectedParentVersion" }
            .All(field => Comparable(before.Payload, field) == Comparable(json.RootElement, field));
    }

    private static string? Comparable(JsonElement input, string name) =>
        !input.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null
            : value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
}
