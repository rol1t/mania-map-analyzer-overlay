(function () {
  "use strict";

  if (window.__overlayHostRuntime && typeof window.__overlayHostRuntime.dispose === "function") {
    window.__overlayHostRuntime.dispose();
  }

  const config = window.__overlayHostConfig || {};
  const overlayMode = config.overlayMode === true;
  const card = document.querySelector(config.hostSelector || "body");
  const disposers = [];
  let resizeObserver = null;
  let mutationObserver = null;
  let reportFrame = 0;
  let resizeGestureTimer = 0;

  function send(message) {
    try {
      if (typeof invokeCSharpAction === "function") {
        invokeCSharpAction(message);
      } else if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(message);
      }
    } catch (exception) {
      console.error("Overlay native bridge message failed", exception);
    }
  }

  window.__overlayHostSend = send;

  function listen(target, eventName, listener, options) {
    target.addEventListener(eventName, listener, options);
    disposers.push(function () { target.removeEventListener(eventName, listener, options); });
  }

  listen(window, "overlay:runtime-error", function (event) {
    const detail = event && event.detail || {};
    const operation = String(detail.operation || "Overlay runtime");
    const message = String(detail.message || "Unknown runtime error");
    try { send("overlay:error:" + encodeURIComponent(operation + " — " + message)); }
    catch (exception) { console.error("Reporting overlay runtime error failed", exception); }
  });

  function clearResizeGesture() {
    window.__overlayResizeGestureUntil = 0;
    if (resizeGestureTimer) clearTimeout(resizeGestureTimer);
    resizeGestureTimer = 0;
  }

  function markResizeGesture() {
    window.__overlayResizeGestureUntil = Date.now() + 500;
    if (resizeGestureTimer) clearTimeout(resizeGestureTimer);
    resizeGestureTimer = window.setTimeout(clearResizeGesture, 550);
  }

  function reportSize() {
    reportFrame = 0;
    if (!card) return;
    const bounds = card.getBoundingClientRect();
    const style = getComputedStyle(card);
    const scaling = Math.max(1, window.devicePixelRatio || 1);
    send("overlay:size:"
      + Math.ceil(bounds.width * scaling) + ","
      + Math.ceil(bounds.height * scaling) + ","
      + ((Number.parseFloat(style.borderTopLeftRadius) || 0) * scaling));
  }

  function queueSizeReport() {
    if (reportFrame) return;
    reportFrame = requestAnimationFrame(reportSize);
  }

  function resizeDirection(event) {
    if (!card || event.target && event.target.closest && event.target.closest(".overlay-resize-handle")) return "";
    const bounds = card.getBoundingClientRect();
    const scaling = Math.max(1, window.devicePixelRatio || 1);
    const edge = Math.max(10, Math.min(14, 12 / scaling));
    if (event.clientX < bounds.left || event.clientX > bounds.right
      || event.clientY < bounds.top || event.clientY > bounds.bottom) return "";
    const north = event.clientY - bounds.top <= edge;
    const south = bounds.bottom - event.clientY <= edge;
    const west = event.clientX - bounds.left <= edge;
    const east = bounds.right - event.clientX <= edge;
    return (north ? "n" : south ? "s" : "") + (west ? "w" : east ? "e" : "");
  }

  function removeResizeHandles() {
    document.querySelectorAll(".overlay-resize-handle").forEach(function (handle) { handle.remove(); });
    const style = document.getElementById("overlay-resize-handle-style");
    if (style) style.remove();
  }

  function ensureResizeHandles() {
    if (!overlayMode) {
      removeResizeHandles();
      return;
    }

    const root = document.body || document.documentElement;
    if (!root) return;
    let style = document.getElementById("overlay-resize-handle-style");
    if (!style) {
      style = document.createElement("style");
      style.id = "overlay-resize-handle-style";
      style.textContent = config.resizeHandleCss || "";
      (document.head || root).appendChild(style);
    }

    ["n", "s", "e", "w", "nw", "ne", "se", "sw"].forEach(function (direction) {
      let handle = document.querySelector(`.overlay-resize-handle[data-direction="${direction}"]`);
      if (!handle) {
        handle = document.createElement("div");
        handle.className = "overlay-resize-handle";
        handle.setAttribute("data-direction", direction);
        handle.setAttribute("aria-hidden", "true");
        root.appendChild(handle);
      }
      if (handle.__overlayResizeBound) return;
      handle.__overlayResizeBound = true;
      const begin = function (event) {
        if (event.button !== undefined && event.button !== 0) return;
        event.preventDefault();
        event.stopPropagation();
        if (event.stopImmediatePropagation) event.stopImmediatePropagation();
        const now = Date.now();
        if (now - (handle.__overlayResizeLast || 0) < 120) return;
        handle.__overlayResizeLast = now;
        markResizeGesture();
        send(`overlay:resize:${direction}`);
      };
      listen(handle, "pointerdown", begin, { capture: true, passive: false });
      listen(handle, "mousedown", begin, { capture: true, passive: false });
    });
  }

  if (card) {
    card.setAttribute("unselectable", "on");
    card.ondragstart = function () { return false; };
    card.onselectstart = function () { return false; };

    if (overlayMode) {
      ensureResizeHandles();
      listen(document, "mousedown", function (event) {
        if (event.button !== 0) return;
        const resizeHandle = event.target && event.target.closest && event.target.closest(".overlay-resize-handle");
        if (resizeHandle || Date.now() < (window.__overlayResizeGestureUntil || 0)) {
          event.preventDefault();
          return;
        }
        const direction = resizeDirection(event);
        if (direction) {
          markResizeGesture();
          send(`overlay:resize:${direction}`);
        } else {
          send("overlay:drag");
        }
      }, true);
      listen(document, "wheel", function (event) {
        if (!event.ctrlKey) return;
        event.preventDefault();
        const now = Date.now();
        if (now - (window.__overlayScaleWheelAt || 0) < 160) return;
        window.__overlayScaleWheelAt = now;
        send(`overlay:scale:${event.deltaY < 0 ? "5" : "-5"}`);
      }, { capture: true, passive: false });
      ["pointerup", "pointercancel", "mouseup"].forEach(function (eventName) {
        listen(document, eventName, clearResizeGesture, true);
      });
      listen(window, "blur", clearResizeGesture, true);
    } else {
      removeResizeHandles();
    }

    listen(window, "resize", queueSizeReport);
    listen(window, "overlay:gameplay-state", function (event) {
      const state = event && event.detail || {};
      if (typeof state.isPlaying === "boolean") send(`overlay:play:${state.isPlaying ? "1" : "0"}`);
      if (typeof state.isFocused === "boolean") {
        document.documentElement.classList.toggle("overlay-osu-focused", state.isFocused);
        send(`overlay:focus:${state.isFocused ? "1" : "0"}`);
      }
    });

    if (window.ResizeObserver) {
      resizeObserver = new ResizeObserver(queueSizeReport);
      resizeObserver.observe(card);
    }
    mutationObserver = new MutationObserver(queueSizeReport);
    mutationObserver.observe(card, { attributes: true, childList: true, characterData: true, subtree: true });
    queueSizeReport();
    window.setTimeout(queueSizeReport, 120);
    window.setTimeout(queueSizeReport, 600);
  }

  window.__overlayHostRuntime = {
    dispose: function () {
      disposers.splice(0).forEach(function (dispose) {
        try { dispose(); }
        catch (exception) { console.error("Disposing overlay event listener failed", exception); }
      });
      if (resizeObserver) resizeObserver.disconnect();
      if (mutationObserver) mutationObserver.disconnect();
      if (reportFrame) cancelAnimationFrame(reportFrame);
      clearResizeGesture();
      removeResizeHandles();
      if (window.__overlayHostSend === send) delete window.__overlayHostSend;
    },
  };
})();
