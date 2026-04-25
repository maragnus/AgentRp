const stateByWorkspace = new WeakMap();
const snapThresholdPercent = 3;

export function initialize(workspace, circle, dotNetReference, focusXPercent, focusYPercent, zoomPercent) {
    if (!workspace || !circle)
        return;

    dispose(workspace);

    const state = {
        workspace,
        circle,
        image: workspace.querySelector(".app-image-crop-image"),
        dotNetReference,
        focusXPercent,
        focusYPercent,
        zoomPercent,
        activePointerId: null,
        mode: null,
        startX: 0,
        startY: 0,
        startCropRect: null,
        handle: null
    };

    state.pointerDown = event => handlePointerDown(state, event);
    state.pointerMove = event => handlePointerMove(state, event);
    state.pointerUp = event => handlePointerUp(state, event);
    state.resize = () => {
        if (render(state))
            notify(state);
    };

    circle.addEventListener("pointerdown", state.pointerDown);
    window.addEventListener("resize", state.resize);
    stateByWorkspace.set(workspace, state);
    if (render(state))
        notify(state);
}

export function setCrop(workspace, focusXPercent, focusYPercent, zoomPercent) {
    const state = stateByWorkspace.get(workspace);
    if (!state)
        return;

    state.focusXPercent = focusXPercent;
    state.focusYPercent = focusYPercent;
    state.zoomPercent = clamp(zoomPercent, 100, 300);
    render(state);
    notify(state);
}

export function dispose(workspace) {
    const state = stateByWorkspace.get(workspace);
    if (!state)
        return;

    state.circle.removeEventListener("pointerdown", state.pointerDown);
    state.circle.removeEventListener("pointermove", state.pointerMove);
    state.circle.removeEventListener("pointerup", state.pointerUp);
    state.circle.removeEventListener("pointercancel", state.pointerUp);
    window.removeEventListener("resize", state.resize);
    stateByWorkspace.delete(workspace);
}

function handlePointerDown(state, event) {
    if (event.button !== 0)
        return;

    event.preventDefault();
    const handle = event.target.closest("[data-crop-handle]");
    state.mode = handle ? "resize" : "move";
    state.handle = handle?.dataset.cropHandle ?? null;
    state.activePointerId = event.pointerId;
    state.startX = event.clientX;
    state.startY = event.clientY;
    const imageRect = getImageRect(state);
    state.startCropRect = getCropRect(state, imageRect);
    state.circle.setPointerCapture(event.pointerId);
    state.circle.addEventListener("pointermove", state.pointerMove);
    state.circle.addEventListener("pointerup", state.pointerUp);
    state.circle.addEventListener("pointercancel", state.pointerUp);
}

function handlePointerMove(state, event) {
    if (state.activePointerId !== event.pointerId)
        return;

    event.preventDefault();
    const rect = getImageRect(state);
    const size = Math.min(rect.width, rect.height);
    if (size <= 0)
        return;

    const dxPixels = event.clientX - state.startX;
    const dyPixels = event.clientY - state.startY;

    if (state.mode === "move") {
        const cropRect = clampCropRect({
            left: state.startCropRect.left + dxPixels,
            top: state.startCropRect.top + dyPixels,
            diameter: state.startCropRect.diameter
        }, rect);
        cropRect.left = snapCropCenter(cropRect.left, cropRect.diameter, rect.left, rect.width);
        cropRect.top = snapCropCenter(cropRect.top, cropRect.diameter, rect.top, rect.height);
        applyCropRect(state, cropRect, rect);
    } else if (state.mode === "resize") {
        applyCropRect(state, resizeCropRectFromHandle(state, event, rect), rect);
    }

    render(state);
    notify(state);
}

function handlePointerUp(state, event) {
    if (state.activePointerId !== event.pointerId)
        return;

    state.circle.releasePointerCapture(event.pointerId);
    state.circle.removeEventListener("pointermove", state.pointerMove);
    state.circle.removeEventListener("pointerup", state.pointerUp);
    state.circle.removeEventListener("pointercancel", state.pointerUp);
    state.activePointerId = null;
    state.mode = null;
    state.handle = null;
    state.startCropRect = null;
    state.workspace.classList.remove("app-image-crop-snap-x", "app-image-crop-snap-y");
}

function render(state) {
    const previousFocusXPercent = state.focusXPercent;
    const previousFocusYPercent = state.focusYPercent;
    const previousZoomPercent = state.zoomPercent;
    const imageRect = getImageRect(state);
    const workspaceRect = state.workspace.getBoundingClientRect();
    const cropRect = normalizeCrop(state, imageRect);
    const diameterPixels = cropRect.diameter;
    const leftPixels = cropRect.left - workspaceRect.left + diameterPixels / 2;
    const topPixels = cropRect.top - workspaceRect.top + diameterPixels / 2;
    state.circle.style.width = `${diameterPixels}px`;
    state.circle.style.height = `${diameterPixels}px`;
    state.circle.style.left = `${leftPixels}px`;
    state.circle.style.top = `${topPixels}px`;
    state.workspace.classList.toggle("app-image-crop-snap-x", isCropCentered(cropRect.left, cropRect.diameter, imageRect.left, imageRect.width));
    state.workspace.classList.toggle("app-image-crop-snap-y", isCropCentered(cropRect.top, cropRect.diameter, imageRect.top, imageRect.height));
    return previousFocusXPercent !== state.focusXPercent
        || previousFocusYPercent !== state.focusYPercent
        || previousZoomPercent !== state.zoomPercent;
}

function notify(state) {
    state.dotNetReference.invokeMethodAsync(
        "UpdateDraftCrop",
        Math.round(state.focusXPercent),
        Math.round(state.focusYPercent),
        Math.round(state.zoomPercent));
}

function normalizeCrop(state, imageRect) {
    const cropRect = clampCropRect(getCropRect(state, imageRect), imageRect);
    applyCropRect(state, cropRect, imageRect);
    return cropRect;
}

function getCropRect(state, imageRect) {
    const zoomRatio = clamp(state.zoomPercent, 100, 300) / 100;
    const diameter = imageRect.width / zoomRatio;
    const left = imageRect.left + imageRect.width * (zoomRatio - 1) * clamp(state.focusXPercent, 0, 100) / 100 / zoomRatio;
    const top = imageRect.top + imageRect.height * (zoomRatio - imageRect.ratio) * clamp(state.focusYPercent, 0, 100) / 100 / zoomRatio;
    return { left, top, diameter };
}

function applyCropRect(state, cropRect, imageRect) {
    const zoomRatio = imageRect.width / cropRect.diameter;
    state.zoomPercent = clamp(Math.round(zoomRatio * 100), 100, 300);
    state.focusXPercent = cropRectToPositionPercent(cropRect.left - imageRect.left, imageRect.width, zoomRatio, 1);
    state.focusYPercent = cropRectToPositionPercent(cropRect.top - imageRect.top, imageRect.height, zoomRatio, imageRect.ratio);
}

function cropRectToPositionPercent(offsetPixels, sizePixels, zoomRatio, coverRatio) {
    const denominator = zoomRatio - coverRatio;
    if (Math.abs(denominator) <= 0.0001)
        return 50;

    return clamp(Math.round(offsetPixels / sizePixels * 100 * zoomRatio / denominator), 0, 100);
}

function resizeCropRectFromHandle(state, event, imageRect) {
    const start = state.startCropRect;
    const fixed = getFixedCorner(start, state.handle);
    const direction = getHandleDirection(state.handle);
    const requestedDiameter = (
        direction.x * (event.clientX - fixed.x)
        + direction.y * (event.clientY - fixed.y)) / 2;
    const diameter = clamp(
        requestedDiameter,
        getMinCropDiameter(imageRect),
        getMaxCropDiameterForFixedCorner(fixed, direction, imageRect));

    return {
        left: direction.x < 0 ? fixed.x - diameter : fixed.x,
        top: direction.y < 0 ? fixed.y - diameter : fixed.y,
        diameter
    };
}

function getFixedCorner(cropRect, handle) {
    return {
        x: handle === "nw" || handle === "sw" ? cropRect.left + cropRect.diameter : cropRect.left,
        y: handle === "nw" || handle === "ne" ? cropRect.top + cropRect.diameter : cropRect.top
    };
}

function getHandleDirection(handle) {
    return {
        x: handle === "nw" || handle === "sw" ? -1 : 1,
        y: handle === "nw" || handle === "ne" ? -1 : 1
    };
}

function getMaxCropDiameterForFixedCorner(fixed, direction, imageRect) {
    const horizontal = direction.x < 0 ? fixed.x - imageRect.left : imageRect.left + imageRect.width - fixed.x;
    const vertical = direction.y < 0 ? fixed.y - imageRect.top : imageRect.top + imageRect.height - fixed.y;
    return Math.max(getMinCropDiameter(imageRect), Math.min(horizontal, vertical, getMaxCropDiameter(imageRect)));
}

function clampCropRect(cropRect, imageRect) {
    const diameter = clamp(cropRect.diameter, getMinCropDiameter(imageRect), getMaxCropDiameter(imageRect));
    return {
        left: clamp(cropRect.left, imageRect.left, imageRect.left + imageRect.width - diameter),
        top: clamp(cropRect.top, imageRect.top, imageRect.top + imageRect.height - diameter),
        diameter
    };
}

function getMinCropDiameter(imageRect) {
    return imageRect.width / 3;
}

function getMaxCropDiameter(imageRect) {
    return Math.max(getMinCropDiameter(imageRect), Math.min(imageRect.width, imageRect.height));
}

function snapCropCenter(startPixels, diameterPixels, imageStartPixels, imageSizePixels) {
    const centerPercent = (startPixels + diameterPixels / 2 - imageStartPixels) / imageSizePixels * 100;
    if (Math.abs(centerPercent - 50) > snapThresholdPercent)
        return startPixels;

    return imageStartPixels + imageSizePixels / 2 - diameterPixels / 2;
}

function isCropCentered(startPixels, diameterPixels, imageStartPixels, imageSizePixels) {
    return Math.abs(startPixels + diameterPixels / 2 - imageStartPixels - imageSizePixels / 2) <= 0.01;
}

function getImageRect(state) {
    const workspaceRect = state.workspace.getBoundingClientRect();
    const naturalWidth = state.image?.naturalWidth || 1;
    const naturalHeight = state.image?.naturalHeight || 1;
    const imageRatio = naturalWidth / naturalHeight;
    const workspaceRatio = workspaceRect.width / workspaceRect.height;
    let width = workspaceRect.width;
    let height = workspaceRect.height;
    let left = workspaceRect.left;
    let top = workspaceRect.top;

    if (workspaceRatio > imageRatio) {
        height = workspaceRect.height;
        width = height * imageRatio;
        left += (workspaceRect.width - width) / 2;
    } else {
        width = workspaceRect.width;
        height = width / imageRatio;
        top += (workspaceRect.height - height) / 2;
    }

    return { left, top, width, height, ratio: imageRatio };
}

function clamp(value, min, max) {
    return Math.min(Math.max(value, min), max);
}
