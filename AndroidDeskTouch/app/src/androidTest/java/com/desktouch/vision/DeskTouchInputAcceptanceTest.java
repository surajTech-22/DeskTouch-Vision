package com.desktouch.vision;

import android.app.Instrumentation;
import android.content.Context;
import android.content.Intent;
import android.os.SystemClock;
import android.view.View;
import android.view.inputmethod.InputMethodManager;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;
import java.util.function.BooleanSupplier;
import org.junit.After;
import org.junit.Before;
import org.junit.Test;
import org.junit.runner.RunWith;
import static org.junit.Assert.*;

/**
 * On-device acceptance, NOT mocked: real Android IME input connection,
 * non-privileged Accessibility cursor, click, drag and scroll actions.
 * No network trust is bypassed: this exercises the same app-side functions
 * that the authenticated DeskTouch reverse-* packets invoke.
 */
@RunWith(AndroidJUnit4.class)
public final class DeskTouchInputAcceptanceTest {
    private Instrumentation instrumentation;
    private Context context;
    private InputProbeActivity activity;

    @Before public void setup() {
        instrumentation=InstrumentationRegistry.getInstrumentation();
        context=instrumentation.getTargetContext();
        DeskTouchAccessibilityService.setAllowed(context,true);
        assertTrue("Emulator must enable DeskTouch Accessibility before instrumentation",
            waitUntil(()->DeskTouchAccessibilityService.ready(context),9000));
        assertTrue("Emulator must select DeskTouch IME before instrumentation",
            DeskTouchInputMethod.isSelected(context));
        Intent open=new Intent(context,InputProbeActivity.class);
        open.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        activity=(InputProbeActivity)instrumentation.startActivitySync(open);
        instrumentation.waitForIdleSync();
        assertNotNull(activity.editor);
    }

    @After public void tearDown() {
        DeskTouchAccessibilityService.endRemote();
        if(activity!=null) {
            instrumentation.runOnMainSync(activity::finish);
            instrumentation.waitForIdleSync();
        }
    }

    private boolean waitUntil(BooleanSupplier predicate,long timeoutMs) {
        long stop=SystemClock.uptimeMillis()+timeoutMs;
        while(SystemClock.uptimeMillis()<stop) {
            if(predicate.getAsBoolean())return true;
            SystemClock.sleep(120);
        }
        return predicate.getAsBoolean();
    }

    private void focusEditor() {
        instrumentation.runOnMainSync(()->{
            activity.editor.requestFocus();
            InputMethodManager manager=
                (InputMethodManager)activity.getSystemService(Context.INPUT_METHOD_SERVICE);
            manager.showSoftInput(activity.editor,InputMethodManager.SHOW_IMPLICIT);
        });
        assertTrue("DeskTouch IME did not receive the real input connection",
            waitUntil(DeskTouchInputMethod::hasActiveEditor,8000));
    }

    private String editorText() {
        AtomicReference<String> value=new AtomicReference<>();
        instrumentation.runOnMainSync(()->value.set(activity.editor.getText().toString()));
        return value.get();
    }

    private boolean type(String value) {
        AtomicBoolean accepted=new AtomicBoolean(false);
        instrumentation.runOnMainSync(()->accepted.set(
            DeskTouchInputMethod.commitRemoteText(value)));
        return accepted.get();
    }
    private boolean key(int vk) {
        AtomicBoolean accepted=new AtomicBoolean(false);
        instrumentation.runOnMainSync(()->accepted.set(
            DeskTouchInputMethod.sendRemoteKey(vk)));
        return accepted.get();
    }

    @Test public void realImeTypesEditsAndMovesCaret() {
        focusEditor();
        assertTrue("IME did not accept typed text",type("hello"));
        assertTrue(waitUntil(()->editorText().equals("hello"),3000));
        assertTrue("Backspace rejected",key(8));
        assertTrue(waitUntil(()->editorText().equals("hell"),3000));
        assertTrue(type("o"));
        assertTrue(key(37)); // move left
        assertTrue(type("!"));
        assertTrue("Editor must receive caret movement, not append-only text",
            waitUntil(()->editorText().equals("hell!o"),3000));
    }

    @Test public void remotePointerClicksActualAndroidButton() {
        int[] at=new int[2];
        instrumentation.runOnMainSync(()->activity.clickTarget.getLocationOnScreen(at));
        int x=at[0]+activity.clickTarget.getWidth()/2;
        int y=at[1]+activity.clickTarget.getHeight()/2;
        assertTrue("Accessibility pointer refused approved remote input",
            DeskTouchAccessibilityService.pointer(x,y,null));
        DeskTouchAccessibilityService.mouseButton(1,true);
        DeskTouchAccessibilityService.mouseButton(1,false);
        assertTrue("Actual Android Button never received remote click",
            waitUntil(()->activity.clicks.get()>0,5000));
    }

    @Test public void remotePointerDragsAndScrollsRealViews() {
        int[] drag=new int[2];
        instrumentation.runOnMainSync(()->activity.dragTarget.getLocationOnScreen(drag));
        int sx=drag[0]+activity.dragTarget.getWidth()/3;
        int sy=drag[1]+activity.dragTarget.getHeight()/2;
        int ex=sx+activity.dragTarget.getWidth()/3;
        assertTrue(DeskTouchAccessibilityService.pointer(sx,sy,null));
        DeskTouchAccessibilityService.mouseButton(1,true);
        assertTrue(DeskTouchAccessibilityService.pointer(ex,sy,null));
        DeskTouchAccessibilityService.mouseButton(1,false);
        assertTrue("Drag gesture did not reach the Android View",
            waitUntil(()->activity.drags.get()>0,7000));

        int[] scroll=new int[2];
        instrumentation.runOnMainSync(()->activity.scrollTarget.getLocationOnScreen(scroll));
        int cx=scroll[0]+activity.scrollTarget.getWidth()/2;
        int cy=scroll[1]+activity.scrollTarget.getHeight()/2;
        assertTrue(DeskTouchAccessibilityService.pointer(cx,cy,null));
        DeskTouchAccessibilityService.wheel(-120,false); // scroll forward
        assertTrue("Wheel event did not scroll Android ScrollView",
            waitUntil(()->activity.scrollTarget.getScrollY()>0,7000));
    }
}
