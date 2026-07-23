namespace SteamVRTranslator.VibeVoice.Server;

internal static class ManagementPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>VibeVoice ASR Service</title>
  <style>
    :root { color-scheme: light; font-family: "Segoe UI", sans-serif; color: #202622; background: #f5f7f5; }
    body { margin: 0; }
    main { width: min(780px, calc(100% - 40px)); margin: 36px auto; }
    h1 { font-size: 22px; margin: 0 0 4px; }
    p { color: #65706a; margin: 0 0 22px; }
    section { background: white; border: 1px solid #d8ded9; padding: 20px; margin: 12px 0; }
    .status { display: grid; grid-template-columns: 170px 1fr; gap: 9px 16px; }
    .status span:nth-child(odd), label { color: #65706a; }
    .form { display: grid; grid-template-columns: 170px 1fr; gap: 12px 16px; align-items: center; }
    select, input { height: 36px; border: 1px solid #bcc6bf; padding: 0 10px; font: inherit; }
    .actions { display: flex; gap: 8px; margin-top: 18px; }
    button { min-height: 36px; border: 1px solid #aebbb3; background: white; padding: 0 14px; font: inherit; cursor: pointer; }
    button.primary { color: white; border-color: #087e72; background: #087e72; }
    progress { width: 100%; height: 8px; margin-top: 16px; accent-color: #087e72; }
    #error { color: #b12f34; white-space: pre-wrap; margin-top: 10px; }
  </style>
</head>
<body>
<main>
  <h1>VibeVoice ASR Service</h1>
  <p>Independent resident ASR service for local or remote SteamVR Translator clients.</p>
  <section class="status">
    <span>Service state</span><strong id="state">Loading...</strong>
    <span>Runtime</span><strong id="runtime">-</strong>
    <span>Q4_K model</span><strong id="model">-</strong>
    <span>Data directory</span><code id="data">-</code>
  </section>
  <section>
    <div class="form">
      <label for="backend">Inference backend</label>
      <select id="backend"><option value="cpu">CPU</option><option value="vulkan">Vulkan</option><option value="cuda">CUDA</option></select>
      <label for="device">GPU device index</label><input id="device" type="number" min="0" value="0">
      <label for="threads">CPU threads</label><input id="threads" type="number" min="1" value="4">
      <label for="source">Model source</label><select id="source"><option value="official">Hugging Face</option><option value="hf-mirror">HF Mirror</option></select>
      <label for="key">API key</label><input id="key" type="password" autocomplete="off" placeholder="Required only when configured">
    </div>
    <div class="actions">
      <button class="primary" onclick="install()">Download and start</button>
      <button onclick="configure()">Apply</button>
      <button onclick="runtime('start')">Start</button>
      <button onclick="runtime('stop')">Stop</button>
    </div>
    <progress id="progress" max="1" value="0" hidden></progress>
    <div id="error"></div>
  </section>
</main>
<script>
const key = document.getElementById('key');
const backend = document.getElementById('backend');
const device = document.getElementById('device');
const threads = document.getElementById('threads');
const source = document.getElementById('source');
const state = document.getElementById('state');
const runtimeState = document.getElementById('runtime');
const model = document.getElementById('model');
const data = document.getElementById('data');
const progress = document.getElementById('progress');
const error = document.getElementById('error');
key.value = sessionStorage.getItem('vibevoice-api-key') || '';
key.addEventListener('change', () => sessionStorage.setItem('vibevoice-api-key', key.value));
function headers(json = false) {
  const value = key.value.trim();
  return Object.assign(json ? {'Content-Type':'application/json'} : {}, value ? {'Authorization':'Bearer ' + value} : {});
}
function body() { return {backend:backend.value, deviceIndex:+device.value, threadCount:+threads.value, downloadSource:source.value}; }
async function call(path, options = {}) {
  const response = await fetch(path, options);
  if (!response.ok) throw new Error(await response.text());
  return response.status === 204 ? null : response.json();
}
async function refresh() {
  try {
    const s = await call('/api/v1/status', {headers:headers()});
    state.textContent = s.status + (s.currentOperation ? ' · ' + s.currentOperation : '');
    runtimeState.textContent = s.runtimeInstalled ? (s.runtimeRunning ? 'Running' : 'Installed') : 'Not installed';
    model.textContent = s.modelInstalled ? 'Installed' : 'Not installed';
    data.textContent = s.dataDirectory;
    backend.value = s.backend; device.value = s.deviceIndex; threads.value = s.threadCount; source.value = s.downloadSource;
    error.textContent = s.error || '';
    if (s.currentOperation) {
      progress.hidden = false;
      progress.removeAttribute('value');
      if (s.totalBytes) { progress.value = s.downloadedBytes / s.totalBytes; }
    } else { progress.hidden = true; progress.value = 0; }
  } catch (e) { error.textContent = e.message; }
}
async function install() { try { await call('/api/v1/install',{method:'POST',headers:headers(true),body:JSON.stringify(body())}); } catch(e){error.textContent=e.message;} refresh(); }
async function configure() { try { await call('/api/v1/configure',{method:'POST',headers:headers(true),body:JSON.stringify(body())}); } catch(e){error.textContent=e.message;} refresh(); }
async function runtime(action) { try { await call('/api/v1/runtime/'+action,{method:'POST',headers:headers()}); } catch(e){error.textContent=e.message;} refresh(); }
refresh(); setInterval(refresh, 1000);
</script>
</body>
</html>
""";
}
