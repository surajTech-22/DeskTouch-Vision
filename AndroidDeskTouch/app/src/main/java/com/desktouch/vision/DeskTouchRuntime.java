package com.desktouch.vision;

import android.content.Context;
import java.util.function.Consumer;

/** One process-wide DeskTouch connection shared by foreground service and UI. */
final class DeskTouchRuntime {
    private static DeskTouchSession session;
    private static volatile Consumer<String> listener;
    private static volatile String lastStatus = "DeskTouch ready to find trusted devices.";

    private DeskTouchRuntime() {}
    static synchronized DeskTouchSession session(Context context) {
        if (session == null) {
            session = new DeskTouchSession(new TrustedWindows(context.getApplicationContext()), message -> {
                lastStatus = message;
                Consumer<String> current = listener;
                if (current != null) current.accept(message);
            });
        }
        return session;
    }
    static void setListener(Consumer<String> observer) {
        listener = observer;
        if(observer != null) observer.accept(lastStatus);
    }
    static synchronized void clear() {
        if (session != null) session.close();
        session = null;
        listener = null;
    }
}
