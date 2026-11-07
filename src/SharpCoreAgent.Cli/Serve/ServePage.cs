namespace SharpCoreAgent.Cli;

/// <summary>
/// The page <c>serve</c> answers at <c>GET /</c>: one self-contained document, no other origin.
/// </summary>
/// <remarks>
/// Inline stylesheet and one script, exactly like <see cref="UiPage"/>: the page may open in VS Code's Simple Browser or
/// a Visual Studio browser window, and a link to a CDN would be a request to another origin the panel promised never to
/// make. Every value the script prints is set as text, never as markup, so a tool that returns HTML is shown as HTML
/// rather than obeyed. The script speaks the JSON lines <c>ConsoleObserver --json</c> prints, one per event.
/// </remarks>
internal static class ServePage
{
    /// <summary>Gets the page.</summary>
    internal static string Page { get; } = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="color-scheme" content="light dark">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>sharpcoreagent</title>
        <link rel="icon" href="data:,">
        <style>
        :root{color-scheme:light dark}
        *{box-sizing:border-box}
        body{margin:0;font:15px/1.5 system-ui,sans-serif;display:flex;flex-direction:column;height:100vh;background:#fff;color:#111}
        @media(prefers-color-scheme:dark){body{background:#0e1116;color:#e6edf3}}
        header{display:flex;gap:1rem;align-items:center;padding:.55rem 1rem;border-bottom:1px solid #8884;font-size:.85rem}
        header .sp{flex:1}.stat{color:#888;font-size:.8rem}
        main{flex:1;overflow:auto;padding:1rem;display:flex;flex-direction:column;gap:.75rem}
        .msg{white-space:pre-wrap;word-break:break-word}
        .msg.user{align-self:flex-end;background:#2563eb;color:#fff;padding:.45rem .75rem;border-radius:.6rem;max-width:80%}
        .msg.assistant{align-self:flex-start;max-width:92%}
        .think{color:#888;font-style:italic;white-space:pre-wrap;font-size:.85rem;margin:.2rem 0}
        .tool{border:1px solid #8884;border-radius:.5rem;padding:.4rem .6rem;font-size:.85rem;background:#8881}
        .tool .h{display:flex;gap:.5rem;align-items:center;font-weight:600}
        .tool pre{margin:.35rem 0 0;white-space:pre-wrap;word-break:break-word;font-size:.8rem;max-height:16rem;overflow:auto}
        .ok{color:#16a34a}.err{color:#dc2626}
        .approve{border:1px solid #f59e0b;border-radius:.5rem;padding:.6rem .8rem;background:#f59e0b1a}
        .approve .row{display:flex;gap:.5rem;flex-wrap:wrap;margin-top:.5rem}
        .preview{white-space:pre-wrap;font-family:ui-monospace,monospace;font-size:.78rem;max-height:14rem;overflow:auto;border-left:3px solid #f59e0b;padding-left:.5rem;margin:.4rem 0}
        button{font:inherit;padding:.35rem .7rem;border-radius:.4rem;border:1px solid #8886;background:#8881;color:inherit;cursor:pointer}
        button:disabled{opacity:.5;cursor:default}
        button.go{background:#16a34a;color:#fff;border-color:#16a34a}
        button.no{background:#dc2626;color:#fff;border-color:#dc2626}
        footer{display:flex;gap:.5rem;align-items:flex-end;padding:.5rem .8rem;border-top:1px solid #8884}
        textarea{flex:1;resize:vertical;min-height:2.4rem;max-height:10rem;font:inherit;padding:.5rem;border-radius:.5rem;border:1px solid #8886;background:transparent;color:inherit}
        </style>
        </head>
        <body>
        <header><strong>sharpcoreagent</strong><span class="stat" id="model"></span><span class="sp"></span><span class="stat" id="stat"></span></header>
        <main id="log"></main>
        <footer>
          <textarea id="prompt" placeholder="Ask the agent to change the code. Enter sends, Shift+Enter is a new line."></textarea>
          <button class="go" id="send">Send</button>
        </footer>
        <script>
        "use strict";
        var log=document.getElementById('log'),ta=document.getElementById('prompt'),sendBtn=document.getElementById('send');
        var stat=document.getElementById('stat'),modelEl=document.getElementById('model');
        var busy=false,cur=null,tools={},state={};
        function el(t,c,x){var e=document.createElement(t);if(c)e.className=c;if(x!=null)e.textContent=x;return e;}
        function scroll(){log.scrollTop=log.scrollHeight;}
        function block(c,t){var d=el('div',c,t);log.appendChild(d);scroll();return d;}
        function note(t){block('stat',t);}
        function reset(){cur=null;tools={};}
        function textNode(){if(!cur)cur={};if(!cur.text)cur.text=block('msg assistant');return cur.text;}
        function thinkNode(){if(!cur)cur={};if(!cur.think){var d=el('details');d.appendChild(el('summary','stat','reasoning'));cur.think=el('div','think');d.appendChild(cur.think);log.appendChild(d);scroll();}return cur.think;}
        function setBusy(b){busy=b;sendBtn.disabled=b;}
        function post(url,body){return fetch(url,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body||{})});}
        function footer(){stat.textContent=(state.approval||'')+' \u00b7 '+(state.tokensSpent||0)+' tokens';modelEl.textContent=state.model||'';}
        function send(){var t=ta.value.trim();if(!t||busy)return;ta.value='';block('msg user',t);reset();post('/api/turn',{prompt:t}).then(function(r){if(r.status===409)note('a turn is already running');});scroll();}
        sendBtn.addEventListener('click',send);
        ta.addEventListener('keydown',function(e){if(e.key==='Enter'&&!e.shiftKey){e.preventDefault();send();}});
        function handle(ev){var d;try{d=JSON.parse(ev.data);}catch(_){return;}
          switch(d.type){
            case 'turn_started':setBusy(true);reset();break;
            case 'thinking_delta':thinkNode().textContent+=d.text;break;
            case 'text_delta':textNode().textContent+=d.text;scroll();break;
            case 'tool_started':var c0=block('tool');var h=el('div','h');h.appendChild(el('span',null,'\u23fa '+d.tool));var cs=el('span','stat');h.appendChild(cs);c0.appendChild(h);c0.appendChild(el('pre',null,typeof d.arguments==='string'?d.arguments:JSON.stringify(d.arguments)));tools[d.callId]={card:c0,status:cs,out:c0.querySelector('pre')};break;
            case 'tool_output':var t1=tools[d.callId];if(t1)t1.out.textContent+=d.text;else note(d.tool+': '+d.text);break;
            case 'tool_finished':var t2=tools[d.callId];if(t2){t2.status.textContent=d.ok?'ok':'error';t2.status.className='stat '+(d.ok?'ok':'err');if(d.summary&&!d.ok)t2.card.appendChild(el('div','stat',d.summary));}break;
            case 'approval_requested':approval(d);break;
            case 'approval_resolved':var ac=document.getElementById('ap'+d.id);if(ac)ac.querySelector('.row').textContent='decision: '+d.decision;break;
            case 'context_compacted':note('context: dropped '+d.dropped+', kept '+d.kept);break;
            case 'fallback':note('fallback: '+d.from+' to '+d.to+' ('+d.status+')');break;
            case 'notice':note(d.message);break;
            case 'error':note('error: '+d.message);setBusy(false);break;
            case 'report':setBusy(false);state.tokensSpent=(state.tokensSpent||0)+(d.inputTokens||0)+(d.outputTokens||0);footer();note('\u273b '+d.outcome+' \u00b7 '+d.iterations+' iteration(s)'+(d.unverified?' \u00b7 unverified':''));break;
          }}
        function approval(d){var ap=block('approve');ap.id='ap'+d.id;ap.appendChild(el('div',null,'allow '+d.tool+'? '+(d.summary||'')));if(d.preview)ap.appendChild(el('pre','preview',d.preview));var row=el('div','row');function btn(label,decision,cls){var b=el('button',cls||'',label);b.addEventListener('click',function(){var bs=row.querySelectorAll('button');for(var i=0;i<bs.length;i++)bs[i].disabled=true;post('/api/approval',{id:d.id,decision:decision});});row.appendChild(b);}btn('Run once','approve','go');btn('This tool','tool');btn('All tools','session');btn('Deny','deny','no');ap.appendChild(row);scroll();}
        fetch('/api/state').then(function(r){return r.json();}).then(function(s){state=s;footer();}).catch(function(){});
        var es=new EventSource('/api/events');es.onmessage=handle;es.onerror=function(){};
        </script>
        </body>
        </html>
        """;
}
