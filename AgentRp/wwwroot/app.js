window.agentRp = {
  scrollToBottom(element) {
    if (!element)
      return;

    element.scrollTop = element.scrollHeight;
  },

  registerInlineFooterScrollSnap(scrollContainer, footer) {
    if (!scrollContainer || !footer)
      return {
        dispose() {
        }
      };

    const bottomBufferPixels = 4;
    const idleDelayMilliseconds = 2000;
    const buttonOffsetPixels = 12;
    const host = scrollContainer.closest(".app-chat-workspace") || scrollContainer.parentElement;
    const originalHostPosition = host?.style.position ?? "";
    const shouldRestoreHostPosition = host && !originalHostPosition && window.getComputedStyle(host).position === "static";
    let snapTimeout = 0;
    let animationFrame = 0;
    let visibilityFrame = 0;

    if (shouldRestoreHostPosition)
      host.style.position = "relative";

    const scrollButton = document.createElement("button");
    scrollButton.type = "button";
    scrollButton.className = "btn btn-light rounded-circle shadow border position-absolute d-none";
    scrollButton.title = "Scroll to bottom";
    scrollButton.setAttribute("aria-label", "Scroll to bottom");
    scrollButton.innerHTML = '<i class="fa-regular fa-chevron-down" aria-hidden="true"></i>';
    scrollButton.style.bottom = `${footer.offsetHeight + buttonOffsetPixels}px`;
    scrollButton.style.height = "2.5rem";
    scrollButton.style.left = "50%";
    scrollButton.style.padding = "0";
    scrollButton.style.transform = "translateX(-50%)";
    scrollButton.style.width = "2.5rem";
    scrollButton.style.zIndex = "10";

    host?.appendChild(scrollButton);

    const getDistanceFromBottom = () =>
      scrollContainer.scrollHeight - scrollContainer.clientHeight - scrollContainer.scrollTop;

    const updateButtonVisibility = () => {
      visibilityFrame = 0;
      scrollButton.style.bottom = `${footer.offsetHeight + buttonOffsetPixels}px`;
      scrollButton.classList.toggle(
        "d-none",
        scrollContainer.scrollHeight <= scrollContainer.clientHeight + bottomBufferPixels
          || getDistanceFromBottom() <= bottomBufferPixels);
    };

    const scheduleButtonVisibilityUpdate = () => {
      if (visibilityFrame)
        return;

      visibilityFrame = window.requestAnimationFrame(updateButtonVisibility);
    };

    const snapIfNeeded = () => {
      animationFrame = 0;

      if (getDistanceFromBottom() <= bottomBufferPixels)
        return;

      const footerHeight = footer.offsetHeight;
      if (footerHeight <= 0)
        return;

      const containerRect = scrollContainer.getBoundingClientRect();
      const footerRect = footer.getBoundingClientRect();
      const visibleTop = Math.max(footerRect.top, containerRect.top);
      const visibleBottom = Math.min(footerRect.bottom, containerRect.bottom);
      const visibleHeight = Math.max(0, visibleBottom - visibleTop);

      if (visibleHeight > footerHeight / 2)
        scrollContainer.scrollTo({
          top: scrollContainer.scrollHeight,
          behavior: "smooth"
        });
    };

    const scheduleSnapCheck = () => {
      if (snapTimeout)
        window.clearTimeout(snapTimeout);

      snapTimeout = window.setTimeout(() => {
        snapTimeout = 0;

        if (animationFrame)
          return;

        animationFrame = window.requestAnimationFrame(snapIfNeeded);
      }, idleDelayMilliseconds);
    };

    scrollContainer.addEventListener("scroll", scheduleSnapCheck, { passive: true });
    scrollContainer.addEventListener("scroll", scheduleButtonVisibilityUpdate, { passive: true });
    window.addEventListener("resize", scheduleSnapCheck);
    window.addEventListener("resize", scheduleButtonVisibilityUpdate);
    scrollButton.addEventListener("click", () => {
      scrollContainer.scrollTo({
        top: scrollContainer.scrollHeight,
        behavior: "smooth"
      });
    });

    const resizeObserver = new ResizeObserver(scheduleButtonVisibilityUpdate);
    resizeObserver.observe(scrollContainer);
    resizeObserver.observe(footer);

    const mutationObserver = new MutationObserver(scheduleButtonVisibilityUpdate);
    mutationObserver.observe(scrollContainer, {
      childList: true,
      subtree: true
    });

    scheduleSnapCheck();
    scheduleButtonVisibilityUpdate();

    return {
      dispose() {
        scrollContainer.removeEventListener("scroll", scheduleSnapCheck);
        scrollContainer.removeEventListener("scroll", scheduleButtonVisibilityUpdate);
        window.removeEventListener("resize", scheduleSnapCheck);
        window.removeEventListener("resize", scheduleButtonVisibilityUpdate);
        resizeObserver.disconnect();
        mutationObserver.disconnect();
        scrollButton.remove();

        if (shouldRestoreHostPosition)
          host.style.position = originalHostPosition;

        if (snapTimeout) {
          window.clearTimeout(snapTimeout);
          snapTimeout = 0;
        }

        if (animationFrame) {
          window.cancelAnimationFrame(animationFrame);
          animationFrame = 0;
        }

        if (visibilityFrame) {
          window.cancelAnimationFrame(visibilityFrame);
          visibilityFrame = 0;
        }
      }
    };
  }
};
