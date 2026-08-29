#!/bin/sh
# Creates what this site serves, and the page that points at it.
#
# The payloads are sparse files. They cost nothing to create and nothing on disk, and a tunnel
# carries every byte of them all the same, which is the only property that matters here.

set -eu

mkdir -p /payload

[ -f /payload/download.bin ] || truncate -s "${LAB_DOWNLOAD_SIZE:-100M}" /payload/download.bin
[ -f /payload/stream.bin ] || truncate -s "${LAB_STREAM_SIZE:-512M}" /payload/stream.bin

# A single transparent pixel. It exists so that a page opened from disk can tell whether this site
# answers: an image is the one thing a browser will fetch across origins without asking permission,
# and every other way of asking is refused before it reaches the network.
[ -f /payload/pixel.png ] || printf '%s' \
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==' \
    | base64 -d > /payload/pixel.png

cat > /usr/share/nginx/html/index.html <<PAGE
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${LAB_SITE_NAME}</title>
<style>
  :root { color-scheme: light dark; }
  body { font: 15px/1.55 system-ui, sans-serif; margin: 0; padding: 40px 24px; }
  main { max-width: 640px; margin: 0 auto; }
  h1 { font-size: 22px; margin: 0 0 4px; }
  p.lead { margin: 0 0 28px; opacity: 0.7; }
  section { border-top: 1px solid rgba(128,128,128,0.3); padding: 18px 0; }
  h2 { font-size: 14px; text-transform: uppercase; letter-spacing: 0.06em; opacity: 0.6; margin: 0 0 8px; }
  code { font-family: ui-monospace, Consolas, monospace; }
  button { font: inherit; padding: 7px 14px; border-radius: 7px; border: 1px solid rgba(128,128,128,0.5); background: transparent; color: inherit; cursor: pointer; }
  #rate { font-size: 28px; font-variant-numeric: tabular-nums; margin: 12px 0 0; }
</style>
</head>
<body>
<main>
  <h1>${LAB_SITE_NAME}</h1>
  <p class="lead">Reachable only through lab server ${LAB_INDEX}. If you can read this, the tunnel is carrying traffic.</p>

  <section>
    <h2>Identity</h2>
    <p>This site answers at <code>${LAB_SITE_ADDRESS}</code>. Ask it what it sees:
       <a href="/who">/who</a>, or <a href="/ping">/ping</a> for something small.</p>
  </section>

  <section>
    <h2>Download</h2>
    <p>As fast as the tunnel allows.</p>
    <p><a href="/download.bin">download.bin (${LAB_DOWNLOAD_SIZE:-100M})</a></p>
  </section>

  <section>
    <h2>Stream</h2>
    <p>Served at a fixed ${LAB_STREAM_RATE} per second, which is what a video looks like to a network.</p>
    <button id="start">Start streaming</button>
    <p id="rate">idle</p>
  </section>
</main>

<script>
  // Pulls the rate limited file and reports what actually arrives, so the figure in the client and
  // the figure here can be compared while a tunnel is up.
  const button = document.getElementById('start');
  const readout = document.getElementById('rate');
  let controller = null;

  button.addEventListener('click', async () => {
    if (controller) {
      controller.abort();
      controller = null;
      button.textContent = 'Start streaming';
      readout.textContent = 'idle';
      return;
    }

    controller = new AbortController();
    button.textContent = 'Stop';

    const started = performance.now();
    let received = 0;

    try {
      const response = await fetch('/stream.bin?' + Date.now(), { signal: controller.signal });
      const reader = response.body.getReader();

      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        received += value.length;
        const seconds = (performance.now() - started) / 1000;
        readout.textContent = (received / seconds / 1048576).toFixed(2) + ' MB/s';
      }

      readout.textContent = 'finished';
    } catch {
      readout.textContent = 'stopped';
    } finally {
      controller = null;
      button.textContent = 'Start streaming';
    }
  });
</script>
</body>
</html>
PAGE
