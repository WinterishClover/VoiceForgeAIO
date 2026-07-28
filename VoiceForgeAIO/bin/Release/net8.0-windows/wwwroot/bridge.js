// Thin RPC client over Photino's WebMessage channel.
//
// Protocol: we send {id, method, args} via window.external.sendMessage(...);
// C# replies with {id, ok, result, error} via window.SendWebMessage(...), which
// arrives here through window.external.receiveMessage(...). Unsolicited pushes
// from C# (log lines, batch progress) arrive the same way but with {event, data}
// and no id.

const pending = new Map();
const eventListeners = new Map();
let counter = 0;

function nextId() {
  counter += 1;
  return `req_${Date.now()}_${counter}`;
}

window.external.receiveMessage((raw) => {
  let msg;
  try {
    msg = JSON.parse(raw);
  } catch {
    console.error("Bridge: received non-JSON message", raw);
    return;
  }

  if (msg.id) {
    const entry = pending.get(msg.id);
    if (!entry) return;
    pending.delete(msg.id);
    if (msg.ok) entry.resolve(msg.result);
    else entry.reject(new Error(msg.error || "Unknown error"));
    return;
  }

  if (msg.event) {
    const listeners = eventListeners.get(msg.event) || [];
    for (const fn of listeners) fn(msg.data);
  }
});

/** Calls a C# method by name, returning a Promise that resolves with its result. */
export function call(method, args = {}) {
  return new Promise((resolve, reject) => {
    const id = nextId();
    pending.set(id, { resolve, reject });
    window.external.sendMessage(JSON.stringify({ id, method, args }));
  });
}

/** Subscribes to an unsolicited event pushed from C# (e.g. "log", "batchProgress"). */
export function on(eventName, handler) {
  if (!eventListeners.has(eventName)) eventListeners.set(eventName, []);
  eventListeners.get(eventName).push(handler);
}
