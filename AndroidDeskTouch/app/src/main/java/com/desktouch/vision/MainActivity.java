package com.desktouch.vision;

import android.app.*;
import android.content.*;
import android.graphics.*;
import android.os.*;
import android.provider.Settings;
import android.text.*;
import android.view.*;
import android.widget.*;
import java.util.List;

/** DeskTouch Vision Android: hardware keyboard/mouse settings belong to Android itself. */
public final class MainActivity extends Activity {
    private TextView status,devices;
    private LinearLayout deviceList;
    private TextView connectionSummary;
    private DeskTouchSession session;
    private View pointerPad;
    private float prevX,prevY,downX,downY;
    private boolean dragged;
    private volatile boolean scanning;
    private float pointerSpeed=2.8f;
    private boolean suppressTypedText;

    @Override public void onCreate(Bundle savedState){
        super.onCreate(savedState);
        session=DeskTouchRuntime.session(this);
        DeskTouchRuntime.setListener(message ->
            runOnUiThread(()->{if(status!=null)status.setText(message);}));
        // Android 13+ requires permission to show the persistent connection
        // notification in the shade. The foreground service itself is still
        // subject to Android's background lifecycle rules.
        if(Build.VERSION.SDK_INT>=33 &&
            checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)
                !=android.content.pm.PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{android.Manifest.permission.POST_NOTIFICATIONS},37377);
        }
        if(AutoConnectService.enabled(this)) {
            try {AutoConnectService.start(this);}
            catch(RuntimeException startupError) {
                android.util.Log.w("DeskTouch","Background startup not available",startupError);
            }
        }
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
        hint.setText("One linked network • Windows ↔ Android mouse and keyboard sharing");
        hint.setTextColor(Color.LTGRAY);
        hint.setPadding(0,dp(4),0,dp(12));
        page.addView(hint);
        status=new TextView(this);
        status.setText("Scan for your Windows computer on the same private Wi-Fi.");
        status.setTextColor(Color.rgb(157,203,245));
        status.setTextSize(15);
        status.setPadding(0,dp(6),0,dp(14));
        page.addView(status);
        addButton(page,"Find devices",this::scan);
        addButton(page,"Take control of selected Windows screen",()->{
            DeskTouchRuntime.releaseAll();
            session=DeskTouchRuntime.session(this);
            session.claimControl();
        });
        addButton(page,"Return keyboard and mouse control to this device",()->{
            DeskTouchRuntime.releaseAll();
            status.setText("Remote Windows input returned. Secure connections remain open.");
        });
        connectionSummary=new TextView(this);
        connectionSummary.setTextColor(Color.rgb(171,216,188));
        connectionSummary.setPadding(0,dp(5),0,dp(9));
        connectionSummary.setText("Connected devices: "+DeskTouchRuntime.connectedCount()+" / 8");
        page.addView(connectionSummary);
        deviceList=new LinearLayout(this);
        deviceList.setOrientation(LinearLayout.VERTICAL);
        page.addView(deviceList);
        android.widget.CheckBox allowWindows=new android.widget.CheckBox(this);
        allowWindows.setText("Allow approved Windows PCs to control this Android screen");
        allowWindows.setTextColor(Color.WHITE);
        allowWindows.setChecked(DeskTouchAccessibilityService.allowed(this));
        allowWindows.setOnCheckedChangeListener((v,enabled)->{
            DeskTouchAccessibilityService.setAllowed(this,enabled);
            if(enabled && !DeskTouchAccessibilityService.ready(this)){
                status.setText("Enable 'DeskTouch Windows control' in Android Accessibility settings, then reconnect.");
                startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS));
            }else status.setText(enabled
                ? "Windows control enabled. Reconnect this phone so Windows detects the new capability."
                : "Windows control disabled. Android touch has priority.");
        });
        page.addView(allowWindows);
        addButton(page,"Open Android Accessibility settings",()->
            startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)));
        addButton(page,"Stop Windows control on this phone",()->{
            DeskTouchRuntime.remoteNativeTouch();
            status.setText("Windows input authority returned to Android.");
        });
        addButton(page,"Trust connected PC (auto reconnect identity)",()->session.requestTrust());
        addButton(page,"Disconnect selected device",()->{
            AutoConnectService.pausePeer(this,DeskTouchRuntime.selectedIdentity());
            session.releaseControl();
            session.disconnect();
            status.setText("Selected device disconnected. Other peers stay connected; "+
                "auto reconnect paused only for this device.");
        });
        android.widget.CheckBox autoStart=new android.widget.CheckBox(this);
        autoStart.setText("Launch on Android startup and reconnect trusted devices");
        autoStart.setTextColor(Color.WHITE);
        autoStart.setChecked(AutoConnectService.enabled(this));
        autoStart.setOnCheckedChangeListener((button,checked)->{
            AutoConnectService.setEnabled(this,checked);
            AutoConnectService.setReconnect(this,checked);
            status.setText(checked
                ? "Android startup enabled; trusted PCs reconnect automatically when reachable."
                : "Android startup and background reconnect disabled.");
        });
        page.addView(autoStart);
        addButton(page,"Get updates",()->AndroidUpdater.checkForUpdates(this,true));
        addButton(page,"Update existing application",()->AndroidUpdater.installAvailableUpdate(this));
        // Only checks official GitHub metadata; never downloads or silently
        // installs an executable without explicit user action.
        AndroidUpdater.checkForUpdates(this,false);
        TextView padLabel=new TextView(this);
        padLabel.setText("Mobile touchpad / connected mouse");
        padLabel.setTextColor(Color.WHITE);padLabel.setTextSize(16);
        padLabel.setPadding(0,dp(12),0,dp(6));page.addView(padLabel);
        SeekBar pointerSensitivity=new SeekBar(this);
        pointerSensitivity.setMax(400);pointerSensitivity.setProgress(180);
        pointerSensitivity.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener(){
            public void onProgressChanged(SeekBar b,int progress,boolean fromUser){
                pointerSpeed=1f+progress/100f;
            }
            public void onStartTrackingTouch(SeekBar b){}
            public void onStopTrackingTouch(SeekBar b){}
        });
        page.addView(pointerSensitivity);
        pointerPad=new View(this){
            final Paint paint=new Paint(3);
            @Override protected void onDraw(Canvas c){
                paint.setColor(Color.rgb(49,56,69));
                c.drawRoundRect(0,0,getWidth(),getHeight(),dp(12),dp(12),paint);
                paint.setColor(Color.rgb(200,208,220));
                paint.setTextAlign(Paint.Align.CENTER);paint.setTextSize(dp(14));
                c.drawText("Move to navigate • tap to click",getWidth()/2f,getHeight()/2f,paint);
                c.drawText("Swipe across a group edge to switch PC",getWidth()/2f,
                    getHeight()/2f+dp(22),paint);
            }
            @Override public boolean onCapturedPointerEvent(MotionEvent e){
                if(e.getActionMasked()==MotionEvent.ACTION_MOVE){
                    DeskTouchRuntime.session(MainActivity.this).moveBy(e.getX()*pointerSpeed,e.getY()*pointerSpeed);
                    return true;
                }
                if(e.getActionMasked()==MotionEvent.ACTION_SCROLL){
                    DeskTouchRuntime.session(MainActivity.this).wheel(Math.round(e.getAxisValue(MotionEvent.AXIS_VSCROLL)*120),false);
                    DeskTouchRuntime.session(MainActivity.this).wheel(Math.round(e.getAxisValue(MotionEvent.AXIS_HSCROLL)*120),true);
                    return true;
                }
                if(e.getActionMasked()==MotionEvent.ACTION_BUTTON_PRESS ||
                    e.getActionMasked()==MotionEvent.ACTION_BUTTON_RELEASE){
                    int button=e.getActionButton()==MotionEvent.BUTTON_SECONDARY?2:
                        e.getActionButton()==MotionEvent.BUTTON_TERTIARY?3:1;
                    DeskTouchRuntime.session(MainActivity.this).button(button,e.getActionMasked()==MotionEvent.ACTION_BUTTON_PRESS);
                    return true;
                }
                return super.onCapturedPointerEvent(e);
            }
        };
        pointerPad.setFocusable(true);pointerPad.setFocusableInTouchMode(true);
        page.addView(pointerPad,new LinearLayout.LayoutParams(-1,dp(185)));
        pointerPad.setOnTouchListener((v,event)->{
            switch(event.getActionMasked()){
                case MotionEvent.ACTION_DOWN:
                    prevX=downX=event.getX();prevY=downY=event.getY();dragged=false;
                    v.requestFocus();return true;
                case MotionEvent.ACTION_MOVE:
                    float dx=event.getX()-prevX,dy=event.getY()-prevY;
                    if(Math.abs(event.getX()-downX)+Math.abs(event.getY()-downY)>dp(9))
                        dragged=true;
                    DeskTouchRuntime.session(MainActivity.this).moveBy(dx*pointerSpeed,dy*pointerSpeed);
                    prevX=event.getX();prevY=event.getY();return true;
                case MotionEvent.ACTION_UP:
                    if(!dragged)DeskTouchRuntime.session(MainActivity.this).click(1);
                    v.performClick();return true;
                default:return true;
            }
        });
        addButton(page,"Capture connected physical mouse",()->{
            pointerPad.requestFocus();pointerPad.requestPointerCapture();
            status.setText("Physical mouse captured; use Back to release it.");
        });
        TextView keyboardLabel=new TextView(this);
        keyboardLabel.setText("Type on the selected Windows screen using Android keyboard");
        keyboardLabel.setTextColor(Color.WHITE);page.addView(keyboardLabel);
        EditText keyboardInput=new EditText(this);
        keyboardInput.setHint("Tap to open the Android keyboard");
        keyboardInput.setSingleLine(false);
        keyboardInput.setInputType(android.text.InputType.TYPE_CLASS_TEXT|
            android.text.InputType.TYPE_TEXT_FLAG_MULTI_LINE);
        page.addView(keyboardInput);
        final String[] previousTyped={""};
        keyboardInput.addTextChangedListener(new TextWatcher(){
            public void beforeTextChanged(CharSequence text,int start,int count,int after){}
            public void onTextChanged(CharSequence text,int start,int before,int count){}
            public void afterTextChanged(Editable editable){
                if(suppressTypedText)return;
                String next=editable.toString();
                if(session!=null&&DeskTouchRuntime.session(MainActivity.this).isActive()){
                    int common=0;
                    while(common<previousTyped[0].length()&&common<next.length()&&
                        previousTyped[0].charAt(common)==next.charAt(common))common++;
                    for(int n=common;n<previousTyped[0].length();n++){
                        DeskTouchRuntime.session(MainActivity.this).key(8,true);DeskTouchRuntime.session(MainActivity.this).key(8,false);
                    }
                    if(common<next.length())DeskTouchRuntime.session(MainActivity.this).text(next.substring(common));
                }
                previousTyped[0]=next;
                if(next.length()>800){
                    suppressTypedText=true;
                    editable.clear();previousTyped[0]="";
                    suppressTypedText=false;
                }
            }
        });
        addButton(page,"Clear mobile typing field",()->{
            suppressTypedText=true;
            keyboardInput.setText("");previousTyped[0]="";
            suppressTypedText=false;
        });
        addButton(page,"Android keyboard settings",()->
            startActivity(new Intent(Settings.ACTION_INPUT_METHOD_SETTINGS)));
        addButton(page,"Android Bluetooth keyboard / mouse settings",()->
            startActivity(new Intent(Settings.ACTION_BLUETOOTH_SETTINGS)));
        TextView note=new TextView(this);
        note.setText("Join the linked Windows group once, approving pairing separately on " +
            "each Windows PC. Select an active screen and take input control. " +
            "The mobile keyboard works in the typing field while a Windows claim is active. " +
            "For Windows to control Android, enable Accessibility above; ordinary editable " +
            "non-password fields are supported. Android remains in control of local touch.");
        note.setTextColor(Color.LTGRAY);
        note.setPadding(0,dp(14),0,0);
        page.addView(note);

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
        status.setText("Finding trusted Windows PCs; Windows will control this phone, not vice versa...");
        deviceList.removeAllViews();
        new Thread(()->{
            try{
                List<DesktopDiscovery.Device> list=DesktopDiscovery.scan();
                runOnUiThread(()->{
                    connectionSummary.setText("Connected devices: "+
                        DeskTouchRuntime.connectedCount()+" / 8");
                    if(list.isEmpty())status.setText("No devices found. Check trusted Wi-Fi and DeskTouch tray/background service.");
                    else status.setText("DeskTouch found "+list.size()+" nearby device(s). "+
                        "Interconnected PCs are grouped; connect the network once.");
                    // One visible connection card per linked Windows network.
                    // Direct TLS identities remain separate underneath; a group
                    // must never be substituted for a certificate fingerprint.
                    java.util.LinkedHashMap<String,List<DesktopDiscovery.Device>> groups=
                        DesktopDiscovery.windowsGroups(list);
                    java.util.Set<String> connectedFingerprints=new java.util.HashSet<>();
                    for(DesktopDiscovery.Device device:list)
                        if(!device.android&&DeskTouchRuntime.isConnected(device.fingerprint))
                            connectedFingerprints.add(device.fingerprint);
                    java.util.Set<String> approved=new java.util.HashSet<>();
                    try {approved.addAll(new TrustedWindows(this).fingerprints());}
                    catch(Exception ignored){}
                    long activeGroups=groups.values().stream().filter(members->
                        members.stream().anyMatch(m->connectedFingerprints.contains(m.fingerprint)))
                        .count();
                    connectionSummary.setText("Connected Windows networks: "+
                        activeGroups+" / "+groups.size());
                    DeskTouchRuntime.rememberGroups(groups);
                    for(List<DesktopDiscovery.Device> members:groups.values()){
                        long online=members.stream().filter(m->
                            DeskTouchRuntime.isConnected(m.fingerprint)).count();
                        String names=String.join(" + ",members.stream()
                            .map(m->m.name).toArray(String[]::new));
                        String label=(members.size()>1?"Linked Windows network":"Windows PC")
                            +" • "+names+" • "+online+"/"+members.size()+" connected";
                        addButton(deviceList,label,()->{
                            if(online==members.size()){
                                DesktopDiscovery.Device first=DesktopDiscovery.chooseGateway(
                                    members,connectedFingerprints,approved);
                                if(first!=null){
                                    session=DeskTouchRuntime.select(this,first.fingerprint);
                                    session.claimControl();
                                    status.setText("Windows network connected. Selected "+first.name+
                                        " for keyboard and mouse.");
                                }
                                return;
                            }
                            new AlertDialog.Builder(this)
                                .setTitle("Connect the linked Windows network")
                                .setMessage("Connect "+names+" using one group action. " +
                                    "Each Windows PC will verify its own permanent TLS " +
                                    "identity and require its own first-time pairing approval. " +
                                    "All connected members remain online together.")
                                .setNegativeButton("Cancel",null)
                                .setPositiveButton("Connect all",(dialog,which)->{
                                    android.graphics.Point size=new android.graphics.Point();
                                    getWindowManager().getDefaultDisplay().getRealSize(size);
                                    AutoConnectService.setReconnect(this,true);
                                    int requested=0;
                                    for(DesktopDiscovery.Device member:members){
                                        if(DeskTouchRuntime.isConnected(member.fingerprint)||
                                            DeskTouchRuntime.isConnecting(member.fingerprint))
                                            continue;
                                        if(!member.ready)continue;
                                        try {
                                            AutoConnectService.resumePeer(this,member.fingerprint);
                                            DeskTouchRuntime.forPeer(this,member.fingerprint)
                                                .connect(member,size.x,size.y);
                                            requested++;
                                        }catch(IllegalStateException limit){
                                            status.setText("Eight device limit reached; " +
                                                "existing connections remain available.");
                                            break;
                                        }
                                    }
                                    DesktopDiscovery.Device preferred=DesktopDiscovery.chooseGateway(
                                        members,connectedFingerprints,approved);
                                    if(preferred!=null)
                                        session=DeskTouchRuntime.select(this,preferred.fingerprint);
                                    status.setText("Connecting "+requested+" Windows network " +
                                        "member(s); accept first-time pairing on each PC.");
                                }).show();
                        });
                        if(members.size()>1){
                            addButton(deviceList,"Choose active Windows screen (same network)",()->{
                                String[] choices=members.stream().map(m->m.name+
                                    (DeskTouchRuntime.isConnected(m.fingerprint)
                                        ?" • ready":" • not connected"))
                                    .toArray(String[]::new);
                                new AlertDialog.Builder(this)
                                    .setTitle("Active screen in "+names)
                                    .setItems(choices,(dialog,index)->{
                                        DesktopDiscovery.Device member=members.get(index);
                                        if(!DeskTouchRuntime.isConnected(member.fingerprint)){
                                            status.setText("Connect this member through the " +
                                                "linked network first.");
                                            return;
                                        }
                                        session=DeskTouchRuntime.select(this,member.fingerprint);
                                        session.claimControl();
                                        status.setText("Requesting mouse and keyboard on "+member.name);
                                    }).show();
                            });
                        }
                    }
                    // Android peers remain individually identifiable: only
                    // interconnected Windows PCs share a network grouping.
                    for(DesktopDiscovery.Device d:list)
                        if(d.android)
                            addButton(deviceList,"Android • "+d.name+
                                " • discoverable only",()->status.setText(
                                "Select Windows control from that Android device."));
                });
            }catch(Exception e){runOnUiThread(()->status.setText("LAN scan failed: "+e.getMessage()));}
            finally{scanning=false;}
        },"DeskTouch LAN discovery").start();
    }
    @Override public boolean dispatchKeyEvent(KeyEvent e){
        // The Android IME EditText handles composed Unicode text through
        // TextWatcher. Hardware keyboards control Windows when the touchpad
        // has focus, without stealing typing from the IME.
        if(session!=null && DeskTouchRuntime.session(MainActivity.this).isActive() &&
            !(getCurrentFocus() instanceof EditText) &&
            (e.getSource()&InputDevice.SOURCE_KEYBOARD)==InputDevice.SOURCE_KEYBOARD){
            int vk=toWindowsKey(e.getKeyCode());
            if(vk!=0 && (e.getAction()==KeyEvent.ACTION_DOWN ||
                e.getAction()==KeyEvent.ACTION_UP)){
                if(e.getRepeatCount()==0)DeskTouchRuntime.session(MainActivity.this).key(vk,
                    e.getAction()==KeyEvent.ACTION_DOWN);
                return true;
            }
        }
        return super.dispatchKeyEvent(e);
    }
    private static int toWindowsKey(int c){
        if(c>=KeyEvent.KEYCODE_A&&c<=KeyEvent.KEYCODE_Z)
            return 0x41+c-KeyEvent.KEYCODE_A;
        if(c>=KeyEvent.KEYCODE_0&&c<=KeyEvent.KEYCODE_9)
            return 0x30+c-KeyEvent.KEYCODE_0;
        if(c>=KeyEvent.KEYCODE_F1&&c<=KeyEvent.KEYCODE_F12)
            return 0x70+c-KeyEvent.KEYCODE_F1;
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
        // Opening an Android soft keyboard may temporarily change window
        // focus. It must not silently revoke the approved Windows input
        // lease before IME text is committed. Explicit Return / Disconnect
        // and physical Windows input still revoke that lease.
    }
    @Override protected void onDestroy(){
        DeskTouchRuntime.setListener(null);
        // The foreground reconnect service owns the authenticated TLS sessions.
        // Closing this setup screen must not disconnect the selected phone.
        super.onDestroy();
    }
}
