package com.desktouch.vision;

import android.app.*;
import android.content.*;
import android.graphics.*;
import android.os.*;
import android.provider.Settings;
import android.view.*;
import android.widget.*;
import java.util.List;

/** DeskTouch Vision Android: hardware keyboard/mouse settings belong to Android itself. */
public final class MainActivity extends Activity {
    private TextView status,devices;
    private LinearLayout deviceList;
    private DeskTouchSession session;
    private MobileDiscoveryResponder mobileDiscovery;
    private View pointerPad;
    private float prevX,prevY,downX,downY;
    private boolean dragged;
    private volatile boolean scanning;

    @Override public void onCreate(Bundle savedState){
        super.onCreate(savedState);
        session=new DeskTouchSession(new TrustedWindows(this),message->
            runOnUiThread(()->{if(status!=null)status.setText(message);}));
        LinearLayout page=new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(16),dp(16),dp(16),dp(12));
        page.setBackgroundColor(Color.rgb(30,32,37));
        ScrollView scroll=new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.addView(page);
        setContentView(scroll);
        TextView heading=new TextView(this);
        heading.setText("DeskTouch Vision");
        heading.setTextSize(27);
        heading.setTextColor(Color.WHITE);
        page.addView(heading);
        TextView hint=new TextView(this);
        hint.setText("Windows + Android • same secure DeskTouch connection");
        hint.setTextColor(Color.LTGRAY);
        hint.setPadding(0,dp(4),0,dp(12));
        page.addView(hint);
        status=new TextView(this);
        status.setText("Scan for your Windows computer on the same private Wi-Fi.");
        status.setTextColor(Color.rgb(157,203,245));
        status.setTextSize(15);
        status.setPadding(0,dp(6),0,dp(14));
        page.addView(status);
        addButton(page,"Find Windows computers",this::scan);
        deviceList=new LinearLayout(this);
        deviceList.setOrientation(LinearLayout.VERTICAL);
        page.addView(deviceList);
        addButton(page,"Take control of Windows",()->session.claimControl());
        addButton(page,"Return control to this device",()->session.releaseControl());
        addButton(page,"Trust connected PC (auto reconnect identity)",()->session.requestTrust());
        addButton(page,"Disconnect",()->{session.releaseControl();session.close();session=
            new DeskTouchSession(new TrustedWindows(this),message->
                runOnUiThread(()->status.setText(message)));});
        TextView padLabel=new TextView(this);
        padLabel.setText("Touchpad / external mouse area");
        padLabel.setTextColor(Color.WHITE);
        padLabel.setTextSize(16);
        padLabel.setPadding(0,dp(12),0,dp(8));
        page.addView(padLabel);
        pointerPad=new View(this){
            final Paint paint=new Paint(3);
            @Override protected void onDraw(Canvas c){
                super.onDraw(c);
                paint.setColor(Color.rgb(49,56,69));
                c.drawRoundRect(0,0,getWidth(),getHeight(),dp(12),dp(12),paint);
                paint.setColor(Color.rgb(190,198,214));
                paint.setTextAlign(Paint.Align.CENTER);
                paint.setTextSize(dp(14));
                c.drawText("Move to navigate • tap to click",getWidth()/2f,getHeight()/2f,paint);
                c.drawText("Hardware mouse: enable pointer capture below",getWidth()/2f,getHeight()/2f+dp(22),paint);
            }
            @Override public boolean onCapturedPointerEvent(android.view.MotionEvent e){
                if(e.getActionMasked()==MotionEvent.ACTION_MOVE){
                    session.moveBy(e.getX(),e.getY());
                    return true;
                }
                if(e.getActionMasked()==MotionEvent.ACTION_SCROLL){
                    session.wheel(Math.round(e.getAxisValue(MotionEvent.AXIS_VSCROLL)*120),false);
                    session.wheel(Math.round(e.getAxisValue(MotionEvent.AXIS_HSCROLL)*120),true);
                    return true;
                }
                if(e.getActionMasked()==MotionEvent.ACTION_BUTTON_PRESS||
                   e.getActionMasked()==MotionEvent.ACTION_BUTTON_RELEASE){
                    boolean down=e.getActionMasked()==MotionEvent.ACTION_BUTTON_PRESS;
                    int button=e.getActionButton()==MotionEvent.BUTTON_SECONDARY?2:
                        e.getActionButton()==MotionEvent.BUTTON_TERTIARY?3:1;
                    session.button(button,down);
                    return true;
                }
                return super.onCapturedPointerEvent(e);
            }
        };
        pointerPad.setFocusable(true);
        pointerPad.setFocusableInTouchMode(true);
        pointerPad.setMinimumHeight(dp(170));
        page.addView(pointerPad,new LinearLayout.LayoutParams(-1,dp(190)));
        pointerPad.setOnTouchListener((v,event)->{
            switch(event.getActionMasked()){
                case MotionEvent.ACTION_DOWN:
                    prevX=downX=event.getX();prevY=downY=event.getY();dragged=false;
                    v.requestFocus();
                    return true;
                case MotionEvent.ACTION_MOVE:
                    float dx=event.getX()-prevX,dy=event.getY()-prevY;
                    if(Math.abs(event.getX()-downX)+Math.abs(event.getY()-downY)>dp(9))dragged=true;
                    session.moveBy(dx*1.3f,dy*1.3f);
                    prevX=event.getX();prevY=event.getY();
                    return true;
                case MotionEvent.ACTION_UP:
                    if(!dragged)session.click(1);
                    v.performClick();
                    return true;
                default:return true;
            }
        });
        addButton(page,"Capture connected physical mouse",()->{
            pointerPad.requestFocus();
            pointerPad.requestPointerCapture(); // Android's built-in physical mouse handling.
            status.setText("Mouse capture requested; Back or losing focus releases capture.");
        });
        addButton(page,"Android keyboard settings",()->
            startActivity(new Intent(Settings.ACTION_INPUT_METHOD_SETTINGS)));
        addButton(page,"Android connected device / Bluetooth settings",()->
            startActivity(new Intent(Settings.ACTION_BLUETOOTH_SETTINGS)));
        TextView note=new TextView(this);
        note.setText("Uses Android's own keyboard and mouse settings. DeskTouch does not change " +
            "system input devices or silently enable accessibility. Two-way global Android " +
            "cursor injection is not available to an ordinary app.");
        note.setTextColor(Color.LTGRAY);
        note.setPadding(0,dp(14),0,0);
        page.addView(note);
        mobileDiscovery = new MobileDiscoveryResponder();
        new Thread(() -> {
            try { mobileDiscovery.start(getApplicationContext()); }
            catch (Exception error) {
                runOnUiThread(() -> status.setText(
                    "Android device discovery unavailable: " + error.getMessage()));
            }
        }, "DeskTouch mobile responder start").start();
    }
    private int dp(int size){return Math.round(getResources().getDisplayMetrics().density*size);}
    private void addButton(LinearLayout parent,String label,Runnable action){
        Button b=new Button(this);
        b.setText(label);b.setAllCaps(false);
        b.setOnClickListener(v->action.run());
        parent.addView(b,new LinearLayout.LayoutParams(-1,-2));
    }
    private void scan(){
        if(scanning)return;
        scanning=true;
        status.setText("Looking for Windows DeskTouch on local Wi-Fi...");
        deviceList.removeAllViews();
        new Thread(()->{
            try{
                List<DesktopDiscovery.Device> list=DesktopDiscovery.scan();
                runOnUiThread(()->{
                    if(list.isEmpty())status.setText("No Windows PCs found. Check private Wi-Fi, firewall and DeskTouch tray.");
                    else status.setText("Select a Windows PC below and approve pairing on Windows.");
                    for(DesktopDiscovery.Device d:list){
                        addButton(deviceList,d.name+" • "+d.address,()->{
                            new AlertDialog.Builder(this).setTitle("Secure pairing")
                            .setMessage("Confirm the pairing prompt on your Windows DeskTouch app. " +
                                "The connection checks its TLS certificate fingerprint.")
                            .setNegativeButton("Cancel",null)
                            .setPositiveButton("Connect",(dialog,which)->{
                                android.graphics.Point size=new android.graphics.Point();
                                getWindowManager().getDefaultDisplay().getRealSize(size);
                                session.connect(d,size.x,size.y);
                            }).show();
                        });
                    }
                });
            }catch(Exception e){runOnUiThread(()->status.setText("LAN scan failed: "+e.getMessage()));}
            finally{scanning=false;}
        },"DeskTouch LAN discovery").start();
    }
    @Override public boolean dispatchKeyEvent(KeyEvent e){
        if(session!=null&&session.isActive()&&e.getDevice()!=null&&
           (e.getSource()&InputDevice.SOURCE_KEYBOARD)==InputDevice.SOURCE_KEYBOARD){
            int vk=toWindowsKey(e.getKeyCode());
            if(vk!=0 && (e.getAction()==KeyEvent.ACTION_DOWN||e.getAction()==KeyEvent.ACTION_UP)){
                if(e.getRepeatCount()==0)session.key(vk,e.getAction()==KeyEvent.ACTION_DOWN);
                return true;
            }
        }
        return super.dispatchKeyEvent(e);
    }
    private static int toWindowsKey(int c){
        if(c>=KeyEvent.KEYCODE_A&&c<=KeyEvent.KEYCODE_Z)return 0x41+c-KeyEvent.KEYCODE_A;
        if(c>=KeyEvent.KEYCODE_0&&c<=KeyEvent.KEYCODE_9)return 0x30+c-KeyEvent.KEYCODE_0;
        if(c>=KeyEvent.KEYCODE_F1&&c<=KeyEvent.KEYCODE_F12)return 0x70+c-KeyEvent.KEYCODE_F1;
        switch(c){
            case KeyEvent.KEYCODE_ENTER:return 0x0D;
            case KeyEvent.KEYCODE_DEL:return 0x08;
            case KeyEvent.KEYCODE_TAB:return 0x09;
            case KeyEvent.KEYCODE_ESCAPE:return 0x1B;
            case KeyEvent.KEYCODE_SPACE:return 0x20;
            case KeyEvent.KEYCODE_DPAD_LEFT:return 0x25;
            case KeyEvent.KEYCODE_DPAD_UP:return 0x26;
            case KeyEvent.KEYCODE_DPAD_RIGHT:return 0x27;
            case KeyEvent.KEYCODE_DPAD_DOWN:return 0x28;
            case KeyEvent.KEYCODE_SHIFT_LEFT:return 0xA0;
            case KeyEvent.KEYCODE_SHIFT_RIGHT:return 0xA1;
            case KeyEvent.KEYCODE_CTRL_LEFT:return 0xA2;
            case KeyEvent.KEYCODE_CTRL_RIGHT:return 0xA3;
            case KeyEvent.KEYCODE_ALT_LEFT:return 0xA4;
            case KeyEvent.KEYCODE_ALT_RIGHT:return 0xA5;
            default:return 0;
        }
    }
    @Override public void onWindowFocusChanged(boolean focus){
        super.onWindowFocusChanged(focus);
        if(!focus&&session!=null)session.releaseControl();
    }
    @Override protected void onDestroy(){
        if(mobileDiscovery!=null)mobileDiscovery.close();
        if(session!=null)session.close();
        super.onDestroy();
    }
}
