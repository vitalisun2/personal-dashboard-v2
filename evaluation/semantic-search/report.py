"""Render the frozen corpus, relevance judgments and measured API runs as a local HTML table."""
import argparse
import html
import json
import pathlib

HERE = pathlib.Path(__file__).resolve().parent


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runs", nargs="*", default=[])
    parser.add_argument("--output", default=str(HERE / "report.html"))
    args = parser.parse_args()
    corpus = load(HERE / "fixtures/corpus.json")
    suite = load(HERE / "fixtures/queries.json")
    runs = [load(pathlib.Path(path)) for path in args.runs]
    summary_rows = []
    for run in runs:
        for split in ("dev", "test"):
            summary = run.get("summary", {}).get(split)
            if not summary:
                continue
            def pct(key):
                value = summary.get(key)
                return "—" if value is None else f"{value * 100:.1f}%"
            summary_rows.append("<tr>" + "".join(f"<td>{html.escape(str(value))}</td>" for value in (
                run["label"], split, summary["queries"], pct("recallAt5"), pct("precisionReturned"),
                pct("ndcgAt5"), pct("noAnswerCorrect"), summary["hardNegatives"],
                f"{summary.get('medianMs', 0) / 1000:.2f} с", summary.get("fallbacks", 0))) + "</tr>")
    data = json.dumps({"corpus": corpus, "suite": suite, "runs": runs}, ensure_ascii=False).replace("<", "\\u003c")
    page = '''<!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Проверка смыслового поиска</title><style>
:root{color-scheme:dark;font-family:system-ui,sans-serif;background:#10131b;color:#e5e8f0}body{margin:0;padding:32px;max-width:1600px;margin:auto}h1{font-size:30px;margin:0 0 12px}h2{font-size:22px;margin-top:36px}p{max-width:1050px;line-height:1.6;color:#b7c0d3}a{color:#8fb9ff}table{border-collapse:collapse;width:100%;font-size:14px}th,td{padding:12px;border:1px solid #30384c;text-align:left;vertical-align:top}th{background:#20283a;position:sticky;top:0;z-index:1}td{line-height:1.5}.scroll{overflow:auto;max-height:72vh;border-radius:10px}input,select,button{background:#20283a;color:#e5e8f0;border:1px solid #46546e;border-radius:7px;padding:10px;margin:8px 8px 12px 0}input{width:min(420px,75vw)}.good{color:#80dfb3}.bad{color:#ff9b9b}.muted{color:#929db3;font-size:12px}.pill{display:inline-block;padding:2px 7px;border-radius:5px;background:#273249;margin:2px}details{margin:10px 0;padding:12px;background:#191f2d;border:1px solid #30384c;border-radius:8px}summary{cursor:pointer}.body{white-space:pre-wrap;line-height:1.7;max-width:1000px}.query{min-width:270px}.result{min-width:130px}header{border-bottom:1px solid #30384c;padding-bottom:15px}.legend{display:flex;gap:18px;flex-wrap:wrap}
</style><header><h1>Проверка смыслового поиска</h1><p>48 синтетических записей · 36 фиксированных запросов · 24 для настройки / 12 для финальной проверки. Тексты внесены в отдельный раздел приложения. Эта таблица и правильные ответы в индекс не загружаются.</p>
<p>Три профиля одной реализации: <b>A — baseline</b>, прежний поиск; <b>B — prompted</b>, инструкции Google для запроса и документа, исправленный фильтр; <b>C — reranked</b>, те же векторы и проверка кандидатов Gemma. Дополнительный замер prompted + lead сохраняет старый фильтр для проверки эффекта одних инструкций.</p></header>
<h2>Результаты измерений</h2><div class="scroll"><table><thead><tr><th>Запуск</th><th>Выборка</th><th>Запросов</th><th>Полнота @5</th><th>Точность выданного</th><th>nDCG @5</th><th>Верный пустой ответ</th><th>Сложные ложные совпадения</th><th>Медиана</th><th>Fallback</th></tr></thead><tbody>SUMMARY</tbody></table></div>
<p class="muted">Полнота, nDCG и MRR считаются только для запросов с ответом. Точность выданного — доля подходящих среди реально выданных (до 5); пустой ответ получает 1 только для запроса без ответа. Это не Precision@5. Верный пустой ответ проверяется отдельно; недоступность поиска не считается успехом. Время включает HTTP и подсветку, без гарантии одинакового прогрева моделей. Разметка синтетическая, требует оценки владельца данных.</p>
<h2>Запросы → ожидаемые записи → фактическая выдача</h2><input id="filter" placeholder="Поиск по запросу, категории, ID"><select id="split"><option value="">Все запросы</option><option value="dev">Настройка (dev)</option><option value="test">Проверка (test)</option></select><label><input id="failures" type="checkbox" style="width:auto"> Только расхождения</label>
<div class="legend"><span class="good">Зелёный — подходящая запись</span><span class="bad">Красный — лишняя запись</span><span>Ожидания: 2 — основной ответ, 1 — дополнительный</span></div><p id="count" class="muted"></p><div class="scroll"><table id="queries"></table></div>
<h2>Полные тексты тестовых записей</h2><p>Нейтральные названия исключают подсказку по теме. Название общего раздела одинаково для всех документов. Тематические категории находятся только в таблице запросов.</p><div id="documents"></div>
<script>const DATA=PAYLOAD;
const esc=v=>String(v).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const docs=new Map(DATA.corpus.documents.map(d=>[d.id,d]));
const runs=DATA.runs.map(r=>({...r,map:new Map(r.rows.map(q=>[q.id,q]))}));
function link(id,grade,cls=''){return `<button type="button" class="pill ${cls}" data-doc="${esc(id)}">${esc(id)}${grade?` · ${grade}`:''}</button>`;}
function render(){const term=document.getElementById('filter').value.toLowerCase(),split=document.getElementById('split').value,only=document.getElementById('failures').checked;
const qs=DATA.suite.queries.filter(q=>(!split||q.split===split)&&(!term||JSON.stringify(q).toLowerCase().includes(term))&&(!only||runs.some(r=>{const x=r.map.get(q.id);return x&&(x.metrics.falsePositiveCount>0||(x.metrics.recallAt5!==null&&x.metrics.recallAt5<1));})));
document.getElementById('count').textContent=`Показано ${qs.length} из ${DATA.suite.queries.length}`;
document.getElementById('queries').innerHTML=`<thead><tr><th>ID / выборка</th><th>Запрос и объяснение</th><th>Ожидается</th><th>Похожие, но неверные</th>${runs.map(r=>`<th>${esc(r.label)}</th>`).join('')}</tr></thead><tbody>${qs.map(q=>{const rel=new Set(q.relevant.map(x=>x.id));return `<tr><td>${esc(q.id)}<br><span class="muted">${esc(q.split)}<br>${esc(q.category)}</span></td><td class="query">${esc(q.query)}<details><summary>Почему</summary>${esc(q.reason)}</details></td><td>${q.relevant.map(x=>link(x.id,x.grade)).join('')||'Ничего'}</td><td>${q.hardNegatives.map(x=>link(x)).join('')}</td>${runs.map(r=>{const x=r.map.get(q.id);return `<td class="result">${!x?'—':(x.actual.map(id=>link(id,null,rel.has(id)?'good':'bad')).join('')||'<span class="muted">Пусто</span>')+`<br><span class="muted">${(x.elapsedMs/1000).toFixed(2)} с${x.fallback?' · fallback':''}</span>`}</td>`;}).join('')}</tr>`;}).join('')}</tbody>`;
}
document.getElementById('documents').innerHTML=DATA.corpus.documents.map(d=>`<details id="${esc(d.id)}"><summary><b>${esc(d.id)} · ${esc(d.title)}</b> <span class="muted">${d.body.length} символов</span></summary><div class="body">${esc(d.body)}</div></details>`).join('');
for(const id of ['filter','split','failures'])document.getElementById(id).addEventListener('input',render);render();
document.addEventListener('click',event=>{const button=event.target.closest('button[data-doc]');if(!button)return;const target=document.getElementById(button.dataset.doc);if(target){target.open=true;target.scrollIntoView({block:'start',behavior:'instant'});}});
if(location.hash){const target=document.getElementById(location.hash.slice(1));if(target)target.open=true;}
</script></html>'''
    page = page.replace("SUMMARY", "".join(summary_rows) or '<tr><td colspan="10">Измерения ещё не добавлены.</td></tr>').replace("PAYLOAD", data)
    pathlib.Path(args.output).write_text(page, encoding="utf-8")
    print(args.output)


if __name__ == "__main__":
    main()
