(function () {
  "use strict";

  if (window.__overlayHostRuntime && typeof window.__overlayHostRuntime.dispose === "function") {
    window.__overlayHostRuntime.dispose();
  }

  const config = window.__overlayHostConfig || window._overlayHostConfig || {};
  const overlayMode = config.overlayMode === true;
  const card = document.querySelector(config.hostSelector || "body");
  const disposers = [];
  let resizeObserver = null;
  let mutationObserver = null;
  let reportFrame = 0;
  let delayedReportTimer = 0;
  let lastReportedWidth = 0;
  let lastReportedHeight = 0;
  let dragGesture = null;
  let lastPointerEndAt = 0;
  let lastPointerDownAt = 0;
  let nextDragGestureId = 1;
  const pointerEventsSupported = typeof window.PointerEvent === "function";
  const dragDownEventName = pointerEventsSupported ? "pointerdown" : "mousedown";
  const dragMoveEventName = pointerEventsSupported ? "pointermove" : "mousemove";
  const dragEndEventNames = pointerEventsSupported ? ["pointerup", "pointercancel"] : ["mouseup"];

  function send(message) {
    try {
      if (window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === "function") {
        window.chrome.webview.postMessage(message);
        return true;
      } else if (typeof invokeCSharpAction === "function") {
        invokeCSharpAction(message);
        return true;
      }
    } catch (exception) {
      console.error("Overlay native bridge message failed", exception);
    }
    return false;
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

  function getPointerId(event) {
    const pointerId = Number(event.pointerId);
    return pointerEventsSupported && Number.isInteger(pointerId) ? pointerId : 1;
  }

  // WebView2 exposes screenX/screenY in CSS pixels, just like clientX/clientY.
  // They are therefore converted to Avalonia physical pixels by the host using
  // RenderScaling. Unlike client coordinates, they do not move when this HWND
  // is repositioned underneath the pointer.
  function getScreenPoint(event) {
    const screenX = Number(event.screenX);
    const screenY = Number(event.screenY);
    return Number.isFinite(screenX) && Number.isFinite(screenY)
      ? { screenX: screenX, screenY: screenY }
      : null;
  }

  function getNextDragGestureId() {
    const id = nextDragGestureId;
    nextDragGestureId = nextDragGestureId >= Number.MAX_SAFE_INTEGER ? 1 : nextDragGestureId + 1;
    return id;
  }

  function dragPayload(gesture, sequence, point) {
    return JSON.stringify({
      gestureId: gesture.gestureId,
      pointerId: gesture.pointerId,
      sequence: sequence,
      screenX: point.screenX,
      screenY: point.screenY,
    });
  }

  function releaseDragCapture(gesture) {
    if (!gesture || !gesture.captureTarget || !pointerEventsSupported ||
      typeof gesture.captureTarget.releasePointerCapture !== "function") return;
    try {
      if (!gesture.captureTarget.hasPointerCapture || gesture.captureTarget.hasPointerCapture(gesture.pointerId)) {
        gesture.captureTarget.releasePointerCapture(gesture.pointerId);
      }
    } catch (exception) {
      console.warn("Overlay pointer capture release failed", exception);
    }
  }

  function cancelLocalDrag() {
    releaseDragCapture(dragGesture);
    dragGesture = null;
  }

  function captureDragPointer(event) {
    if (!pointerEventsSupported || !event.target || typeof event.target.setPointerCapture !== "function") {
      return null;
    }
    try {
      event.target.setPointerCapture(event.pointerId);
      return event.target;
    } catch (exception) {
      console.warn("Overlay pointer capture failed", exception);
      return null;
    }
  }

  function endDrag(event) {
    const gesture = dragGesture;
    if (!gesture) return;
    if (event && getPointerId(event) !== gesture.pointerId) return;
    const point = event && getScreenPoint(event) || { screenX: gesture.screenX, screenY: gesture.screenY };
    const sequence = gesture.sequence + 1;
    gesture.sequence = sequence;
    gesture.screenX = point.screenX;
    gesture.screenY = point.screenY;
    send("overlay:drag:end:" + dragPayload(gesture, sequence, point));
    if (pointerEventsSupported && event && event.type.indexOf("pointer") === 0) lastPointerEndAt = Date.now();
    cancelLocalDrag();
  }

  function beginDrag(event) {
    if (event.button !== undefined && event.button !== 0) return;
    const isMouse = event.type.indexOf("mouse") === 0;
    if (isMouse && Date.now() - lastPointerDownAt < 500) return;
    if (!isMouse) lastPointerDownAt = Date.now();
    const bounds = card && card.getBoundingClientRect();
    const reason = !card ? "missing-card" : !card.contains(event.target) ? "outside-card" : "accepted";
    send("overlay:pointerdown:" + JSON.stringify({
      eventType: event.type,
      reason: reason,
      target: event.target && event.target.nodeName || "unknown",
      cardLeft: bounds ? bounds.left : null,
      cardTop: bounds ? bounds.top : null,
      cardRight: bounds ? bounds.right : null,
      cardBottom: bounds ? bounds.bottom : null,
      screenX: Number(event.screenX),
      screenY: Number(event.screenY),
    }));
    if (event.type.indexOf("mouse") === 0 && dragGesture) return;
    if (event.type.indexOf("mouse") === 0 && Date.now() - lastPointerEndAt < 500) return;
    if (reason !== "accepted") return;
    const point = getScreenPoint(event);
    const pointerId = getPointerId(event);
    if (!point || !Number.isInteger(pointerId) || pointerId < 0) return;
    if (dragGesture) endDrag(null);

    const gesture = {
      gestureId: getNextDragGestureId(),
      pointerId: pointerId,
      sequence: 0,
      screenX: point.screenX,
      screenY: point.screenY,
      captureTarget: null,
    };
    if (!send("overlay:drag:start:" + dragPayload(gesture, 0, point))) return;
    dragGesture = gesture;
    dragGesture.captureTarget = captureDragPointer(event);
    event.preventDefault();
  }

  function moveDrag(event) {
    const gesture = dragGesture;
    if (!gesture || getPointerId(event) !== gesture.pointerId) return;
    const point = getScreenPoint(event);
    if (!point) return;
    const sequence = gesture.sequence + 1;
    gesture.sequence = sequence;
    gesture.screenX = point.screenX;
    gesture.screenY = point.screenY;
    if (!send("overlay:drag:move:" + dragPayload(gesture, sequence, point))) {
      cancelLocalDrag();
      return;
    }
    event.preventDefault();
  }

  function beginNativeDrag(event) {
    if (event.button !== undefined && event.button !== 0) return;
    if (!card || !card.contains(event.target)) return;
    event.preventDefault();
    event.stopPropagation();
    if (event.stopImmediatePropagation) event.stopImmediatePropagation();
    send("overlay:native-drag");
  }

  function reportSize() {
    reportFrame = 0;
    if (!card) return;
    const bounds = card.getBoundingClientRect();
    const style = getComputedStyle(card);
    const scaling = Math.max(1, window.devicePixelRatio || 1);
    const width = Math.ceil(bounds.width * scaling);
    const height = Math.ceil(bounds.height * scaling);
    if (width === lastReportedWidth && height === lastReportedHeight) return;
    lastReportedWidth = width;
    lastReportedHeight = height;
    send("overlay:size:"
      + width + ","
      + height + ","
      + ((Number.parseFloat(style.borderTopLeftRadius) || 0) * scaling));
  }

  function queueSizeReport() {
    if (reportFrame || delayedReportTimer) return;
    const scaleAt = Number(window.__overlayScaleWheelAt || 0);
    const remaining = scaleAt > 0 ? 550 - (Date.now() - scaleAt) : 0;
    if (remaining > 0) {
      delayedReportTimer = window.setTimeout(function () {
        delayedReportTimer = 0;
        queueSizeReport();
      }, remaining);
      return;
    }
    reportFrame = requestAnimationFrame(reportSize);
  }

  if (card) {
    card.setAttribute("unselectable", "on");
    card.ondragstart = function () { return false; };
    card.onselectstart = function () { return false; };

    if (overlayMode) {
      listen(window, dragDownEventName, beginNativeDrag, { capture: true, passive: false });
      listen(document, "wheel", function (event) {
        if (!event.ctrlKey) return;
        event.preventDefault();
        const now = Date.now();
        if (now - (window.__overlayScaleWheelAt || 0) < 160) return;
        window.__overlayScaleWheelAt = now;
        send(`overlay:scale:${event.deltaY < 0 ? "5" : "-5"}`);
      }, { capture: true, passive: false });
       const bounds = card.getBoundingClientRect();
       send("overlay:runtime-ready:" + JSON.stringify({
         overlayMode: overlayMode,
         pointerEventsSupported: pointerEventsSupported,
         cardLeft: bounds.left,
         cardTop: bounds.top,
         cardRight: bounds.right,
         cardBottom: bounds.bottom,
         devicePixelRatio: Number(window.devicePixelRatio || 1),
       }));
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
      endDrag(null);
      disposers.splice(0).forEach(function (dispose) {
        try { dispose(); }
        catch (exception) { console.error("Disposing overlay event listener failed", exception); }
      });
      if (resizeObserver) resizeObserver.disconnect();
      if (mutationObserver) mutationObserver.disconnect();
      if (reportFrame) cancelAnimationFrame(reportFrame);
      if (delayedReportTimer) clearTimeout(delayedReportTimer);
      if (window.__overlayHostSend === send) delete window.__overlayHostSend;
    },
  };
})();
