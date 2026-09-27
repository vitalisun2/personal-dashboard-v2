using System.Text.Json;
using PersonalDashboard.V2.Contracts.Chat;

namespace PersonalDashboard.V2.Agent.Application;

public static class ProposalDraftContext
{
    public static ModelMessage ToMessage(ChatProposal proposal)
    {
        var action = proposal.Actions.Single();
        var payload = action.Payload;
        object draft = action.EntityType == "knowledge.document"
            ? new { title = Text(payload, "title"), markdown = Text(payload, "markdown"),
                section = Text(payload, "parentSectionPath") ?? "корень" }
            : action.EntityType == "tasks.task"
                ? new { title = Text(payload, "title"), description = Text(payload, "description") ?? "",
                    destination = Text(payload, "featurePath") is { } path
                        ? Text(payload, "placement") == "backlog" ? "Backlog, связана с фичей " + path : "План: " + path
                        : "Backlog: " + Text(payload, "backlogSectionName") }
                : payload;
        var json = JsonSerializer.Serialize(draft, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        if (action.EntityType != "knowledge.document")
            return new ModelMessage("system", "Ожидающий черновик создания задачи, ещё НЕ сохранён: " + json +
                "\nЕсли пользователь просит изменить задачу, вызови list_task_destinations и prepare_task с полным обновлённым черновиком; сохрани неизменённые поля, включая описание и размещение. Обычный вопрос оставляет черновик как есть. На простое да/создавай без правок напомни нажать кнопку добавления, не вызывай инструмент заново.");
        return new ModelMessage("system", "Ожидающий подтверждения черновик, ещё НЕ сохранён: " + json +
            "\nЕсли пользователь просит поправить документ, ОБЯЗАТЕЛЬНО вызови prepare_knowledge_document с исправленным полным черновиком. " +
            "Сохрани поля, которые не просили менять. Одного текстового ответа недостаточно: он не меняет черновик. " +
            "Обычный вопрос оставляет черновик как есть. На да/создавай без правок напомни нажать кнопку «Создать документ», не вызывай инструмент заново.");
    }

    private static string? Text(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
