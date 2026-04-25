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
    let snapTimeout = 0;
    let animationFrame = 0;

    const snapIfNeeded = () => {
      animationFrame = 0;

      const distanceFromBottom = scrollContainer.scrollHeight - scrollContainer.clientHeight - scrollContainer.scrollTop;
      if (distanceFromBottom <= bottomBufferPixels)
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
    window.addEventListener("resize", scheduleSnapCheck);
    scheduleSnapCheck();

    return {
      dispose() {
        scrollContainer.removeEventListener("scroll", scheduleSnapCheck);
        window.removeEventListener("resize", scheduleSnapCheck);

        if (snapTimeout) {
          window.clearTimeout(snapTimeout);
          snapTimeout = 0;
        }

        if (animationFrame) {
          window.cancelAnimationFrame(animationFrame);
          animationFrame = 0;
        }
      }
    };
  }
};
