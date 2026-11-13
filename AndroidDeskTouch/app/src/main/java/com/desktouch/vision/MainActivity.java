package com.desktouch.vision;

import android.app.*;
import android.content.*;
import android.graphics.*;
import android.os.*;
import android.provider.Settings;
import android.text.*;
import android.view.*;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputConnection;
import android.view.inputmethod.InputConnectionWrapper;
import android.view.inputmethod.InputMethodManager;
import android.widget.*;
import java.util.List;

/** DeskTouch Vision Android: hardware keyboard/mouse settings belong to Android itself. */
public final class MainActivity extends Activity {
    private static final String PREFS="desktouch_prefs";
    private static final String PREF_POINTER_PROGRESS="pointer_sensitivity_progress";
    private TextView status,devices;
    private LinearLayout deviceList,environmentList;
    private TextView connectionSummary;
    private TextView permissionState;
    private TextView trustSummary;
    private CheckBox autoStartCheck;
    private DeskTouchSession session;
    private View pointerPad;
    private float prevX,prevY,downX,downY,twoFingerPrevY,twoFingerScrollCarry;
    private boolean dragged,twoFingerMoved;
    private int maxPointerCount;
    private volatile boolean scanning;
    private float pointerSpeed=2.8f;
    private boolean suppressTypedText;

    @Override public void onCreate(Bundle savedState){
        super.onCreate(savedState);
        int savedProgress=getSharedPreferences(PREFS,MODE_PRIVATE)
            .getInt(PREF_POINTER_PROGRESS,180);
        savedProgress=Math.max(0,Math.min(400,savedProgress));
        pointerSpeed=1f+savedProgress/100f;
        session=DeskTouchRuntime.session(this);
        DeskTouchRuntime.setListener(message ->
            runOnUiThread(()->{
                if(status!=null)status.setText(message);
                if(environmentList!=null)refreshEnvironmentMembers();
                refreshTrustSummary();
                updateVisibleScreenAwake();
            }));
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
        addButton(page,"Take control of active environment screen",()->{
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
        environmentList=new LinearLayout(this);
        environmentList.setOrientation(LinearLayout.VERTICAL);
        page.addView(environmentList);
        refreshEnvironmentMembers();
        android.widget.CheckBox allowWindows=new android.widget.CheckBox(this);
        allowWindows.setText("Allow approved Windows PCs to control this Android screen");
        allowWindows.setTextColor(Color.WHITE);
        allowWindows.setChecked(DeskTouchAccessibilityService.allowed(this));
        allowWindows.setOnCheckedChangeListener((v,enabled)->{
            DeskTouchAccessibilityService.setAllowed(this,enabled);
            DeskTouchRuntime.accessibilityPermissionChanged();
            refreshPermissionState();
            updateVisibleScreenAwake();
            if(enabled && !DeskTouchAccessibilityService.ready(this)){
                status.setText("Enable 'DeskTouch Windows control' in Android Accessibility settings, then reconnect.");
                startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS));
            }else status.setText(enabled
                ? "Windows control enabled. Reconnect this phone so Windows detects the new capability."
                : "Windows control disabled. Android touch has priority.");
        });
        page.addView(allowWindows);
        permissionState=new TextView(this);
        permissionState.setTextColor(Color.rgb(243,208,145));
        permissionState.setPadding(0,dp(5),0,dp(8));
        page.addView(permissionState);
        refreshPermissionState();
        addButton(page,"Restore Android Accessibility permission",()->
            startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)));
        addButton(page,"Open Android Accessibility settings",()->
            startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)));
        // A dedicated input method improves editor typing (including WebViews)
        // without requiring root, developer mode or a Bluetooth pairing.
        // Android always requires the user to enable/select a third-party IME.
        addButton(page,"Enable DeskTouch remote keyboard",()->
            startActivity(new Intent(Settings.ACTION_INPUT_METHOD_SETTINGS)));
        addButton(page,"Select DeskTouch remote keyboard",()->{
            android.view.inputmethod.InputMethodManager manager=
                (android.view.inputmethod.InputMethodManager)
                getSystemService(INPUT_METHOD_SERVICE);
            if(manager!=null)manager.showInputMethodPicker();
        });
        addButton(page,"Stop Windows control on this phone",()->{
            DeskTouchRuntime.remoteNativeTouch();
            status.setText("Windows input authority returned to Android.");
        });
        addButton(page,"Pause for Banking (1 tap)",()->{
            if(DeskTouchAccessibilityService.isPausedForBanking(this)){
                DeskTouchAccessibilityService.setAllowed(this,true);
                refreshPermissionState();
                startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS));
                status.setText("Turn DeskTouch Windows control back on in Accessibility settings.");
            }else{
                DeskTouchAccessibilityService.pauseForBanking(this);
                refreshPermissionState();
                updateVisibleScreenAwake();
                status.setText("Paused for banking. Wi-Fi connection stays ready; tap Resume later.");
            }
        });
        addButton(page,"Trust connected PC (auto reconnect identity)",()->{
            DeskTouchSession current=DeskTouchRuntime.session(this);
            session=current;
            current.requestTrust();
        });
        addButton(page,"Leave shared environment",()->{
            DeskTouchSession current=DeskTouchRuntime.session(this);
            session=current;
            AutoConnectService.pausePeer(this,DeskTouchRuntime.selectedIdentity());
            current.releaseControl();
            current.disconnect();
            refreshTrustSummary();
            status.setText("Selected device disconnected. Other peers stay connected; "+
                "auto reconnect paused only for this device.");
        });
        trustSummary=new TextView(this);
        trustSummary.setTextColor(Color.rgb(185,225,195));
        trustSummary.setPadding(0,dp(4),0,dp(4));
        page.addView(trustSummary);
        addButton(page,"Forget trusted computers",()->{
            new TrustedWindows(this).clearAll();
            refreshTrustSummary();
            status.setText("Cleared saved trusted computers on this phone.");
        });
        autoStartCheck=new android.widget.CheckBox(this);
        autoStartCheck.setText("Launch on Android startup and reconnect trusted devices");
        autoStartCheck.setTextColor(Color.WHITE);
        autoStartCheck.setChecked(AutoConnectService.enabled(this));
        autoStartCheck.setOnClickListener(button->{
            boolean checked=autoStartCheck.isChecked();
            AutoConnectService.setEnabled(this,checked);
            AutoConnectService.setReconnect(this,checked);
            status.setText(checked
                ? "Android startup enabled; trusted PCs reconnect automatically when reachable."
                : "Android startup and background reconnect disabled.");
        });
        page.addView(autoStartCheck);
        refreshTrustSummary();
        addButton(page,"Get updates",()->AndroidUpdater.checkForUpdates(this,true));
        addButton(page,"Update existing application",()->AndroidUpdater.installAvailableUpdate(this));
        // Only checks official GitHub metadata; never downloads or silently
        // installs an executable without explicit user action.
        AndroidUpdater.checkForUpdates(this,false);
        int initialSensitivityProgress=Math.max(0,Math.min(400,
            Math.round((pointerSpeed-1f)*100f)));
        TextView padLabel=new TextView(this);
        padLabel.setText("Mouse pointer sensitivity: "+
            Math.round(pointerSpeed*100f/2.8f)+"%");
        padLabel.setTextColor(Color.WHITE);padLabel.setTextSize(16);
        padLabel.setPadding(0,dp(12),0,dp(6));page.addView(padLabel);
        SeekBar pointerSensitivity=new SeekBar(this);
        pointerSensitivity.setMax(400);pointerSensitivity.setProgress(initialSensitivityProgress);
        pointerSensitivity.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener(){
            public void onProgressChanged(SeekBar b,int progress,boolean fromUser){
                pointerSpeed=1f+progress/100f;
                padLabel.setText("Mouse pointer sensitivity: "+
                    Math.round(pointerSpeed*100f/2.8f)+"%");
                if(fromUser){
                    getSharedPreferences(PREFS,MODE_PRIVATE).edit()
                        .putInt(PREF_POINTER_PROGRESS,progress).apply();
                }
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
                    prevX=downX=event.getX();prevY=downY=event.getY();
                    dragged=false;twoFingerMoved=false;maxPointerCount=1;
                    twoFingerScrollCarry=0f;
                    v.requestFocus();return true;
                case MotionEvent.ACTION_POINTER_DOWN:
                    maxPointerCount=Math.max(maxPointerCount,event.getPointerCount());
                    if(event.getPointerCount()>=2){
                        twoFingerPrevY=(event.getY(0)+event.getY(1))*0.5f;
                        twoFingerScrollCarry=0f;
                    }
                    return true;
                case MotionEvent.ACTION_MOVE:
                    if(event.getPointerCount()>=2){
                        maxPointerCount=Math.max(maxPointerCount,event.getPointerCount());
                        float avgY=(event.getY(0)+event.getY(1))*0.5f;
                        float sdy=avgY-twoFingerPrevY;
                        twoFingerPrevY=avgY;
                        if(Math.abs(sdy)>0.5f){
                            twoFingerScrollCarry+=sdy;
                            if(Math.abs(twoFingerScrollCarry)>dp(6))twoFingerMoved=true;
                            int step=dp(14);
                            while(Math.abs(twoFingerScrollCarry)>=step){
                                int wheelAmount=twoFingerScrollCarry>0 ? 120 : -120;
                                DeskTouchRuntime.session(MainActivity.this).wheel(wheelAmount,false);
                                twoFingerScrollCarry-=twoFingerScrollCarry>0 ? step : -step;
                            }
                        }
                        return true;
                    }
                    if(maxPointerCount>1)return true;
                    float dx=event.getX()-prevX,dy=event.getY()-prevY;
                    if(Math.abs(event.getX()-downX)+Math.abs(event.getY()-downY)>dp(9))
                        dragged=true;
                    DeskTouchRuntime.session(MainActivity.this).moveBy(dx*pointerSpeed,dy*pointerSpeed);
                    prevX=event.getX();prevY=event.getY();return true;
                case MotionEvent.ACTION_UP:
                    if(maxPointerCount>=2){
                        if(!twoFingerMoved)DeskTouchRuntime.session(MainActivity.this).click(2);
                    }else if(!dragged){
                        DeskTouchRuntime.session(MainActivity.this).click(1);
                    }
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
        EditText keyboardInput=new EditText(this){
            @Override public InputConnection onCreateInputConnection(EditorInfo outAttrs){
                InputConnection base=super.onCreateInputConnection(outAttrs);
                if(outAttrs!=null){
                    outAttrs.imeOptions=(outAttrs.imeOptions&~EditorInfo.IME_MASK_ACTION)
                        | EditorInfo.IME_ACTION_NONE | EditorInfo.IME_FLAG_NO_EXTRACT_UI;
                }
                if(base==null)return null;
                return new InputConnectionWrapper(base,true){
                    @Override public boolean deleteSurroundingText(int beforeLength,int afterLength){
                        if(getText()!=null && getText().length()==0 && beforeLength>0){
                            DeskTouchSession s=DeskTouchRuntime.session(MainActivity.this);
                            if(s.isActive()){
                                s.key(0x08,true);s.key(0x08,false);
                                return true;
                            }
                        }
                        return super.deleteSurroundingText(beforeLength,afterLength);
                    }
                    @Override public boolean sendKeyEvent(KeyEvent event){
                        if(event!=null && event.getAction()==KeyEvent.ACTION_DOWN &&
                            event.getKeyCode()==KeyEvent.KEYCODE_DEL &&
                            getText()!=null && getText().length()==0){
                            DeskTouchSession s=DeskTouchRuntime.session(MainActivity.this);
                            if(s.isActive()){
                                s.key(0x08,true);s.key(0x08,false);
                                return true;
                            }
                        }
                        return super.sendKeyEvent(event);
                    }
                };
            }
        };
        keyboardInput.setHint("Tap to open the Android keyboard");
        keyboardInput.setSingleLine(false);
        keyboardInput.setInputType(android.text.InputType.TYPE_CLASS_TEXT|
            android.text.InputType.TYPE_TEXT_FLAG_MULTI_LINE);
        LinearLayout mouseActions=new LinearLayout(this);
        mouseActions.setOrientation(LinearLayout.HORIZONTAL);
        mouseActions.setPadding(0,dp(6),0,dp(10));
        addCompactButton(mouseActions,"Left Click",
            ()->DeskTouchRuntime.session(MainActivity.this).click(1));
        addCompactButton(mouseActions,"Right Click",
            ()->DeskTouchRuntime.session(MainActivity.this).click(2));
        addCompactButton(mouseActions,"Scroll ↑",
            ()->DeskTouchRuntime.session(MainActivity.this).wheel(120,false));
        addCompactButton(mouseActions,"Scroll ↓",
            ()->DeskTouchRuntime.session(MainActivity.this).wheel(-120,false));
        addCompactButton(mouseActions,"Keyboard",()->{
            keyboardInput.requestFocus();
            InputMethodManager imm=(InputMethodManager)getSystemService(Context.INPUT_METHOD_SERVICE);
            if(imm!=null)imm.showSoftInput(keyboardInput,InputMethodManager.SHOW_IMPLICIT);
        });
        page.addView(mouseActions);
        page.addView(keyboardInput);
        final String[] previousTyped={""};
        keyboardInput.addTextChangedListener(new TextWatcher(){
            public void beforeTextChanged(CharSequence text,int start,int count,int after){}
            public void onTextChanged(CharSequence text,int start,int before,int count){}
            public void afterTextChanged(Editable editable){
                if(suppressTypedText)return;
                String next=editable.toString();
                DeskTouchSession active=DeskTouchRuntime.session(MainActivity.this);
                if(active.isActive()){
                    int common=0;
                    while(common<previousTyped[0].length()&&common<next.length()&&
                        previousTyped[0].charAt(common)==next.charAt(common))common++;
                    for(int n=common;n<previousTyped[0].length();n++){
                        active.key(8,true);active.key(8,false);
                    }
                    if(common<next.length())sendRemoteTextWithNewlines(active,next.substring(common));
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
        addButton(page,"Open App Info & Xiaomi Battery settings",()->{
            Intent appInfo=new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                android.net.Uri.fromParts("package",getPackageName(),null));
            startActivity(appInfo);
        });
        TextView note=new TextView(this);
        note.setText("Join one DeskTouch environment hub on your Wi-Fi to share " +
            "keyboard and mouse with Windows without developer mode or cables. " +
            "Xiaomi / Redmi setup tip: in App Info -> tap 3 dots at top-right -> " +
            "Allow restricted settings, and set Battery to No restrictions so " +
            "control and Wi-Fi auto-reconnect stay reliable. " +
            "Use Pause for Banking anytime before opening bank or UPI apps.");
        note.setTextColor(Color.LTGRAY);
        note.setPadding(0,dp(14),0,0);
        page.addView(note);

    }
    @Override public void onConfigurationChanged(android.content.res.Configuration config){
        super.onConfigurationChanged(config);
        DeskTouchRuntime.publishPhoneGeometry();
    }
    private void updateVisibleScreenAwake(){
        // A connected, explicitly enabled phone must not fall asleep while
        // this setup/control screen is visible. Android owns this Activity
        // flag; system/third-party app power policies remain authoritative.
        boolean keep=DeskTouchRuntime.connectedCount()>0 &&
            DeskTouchAccessibilityService.ready(this);
        if(keep)getWindow().addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        else getWindow().clearFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
    }
    @Override protected void onResume(){
        super.onResume();
        updateVisibleScreenAwake();
        refreshPermissionState();
        refreshTrustSummary();
        if(autoStartCheck!=null)autoStartCheck.setChecked(AutoConnectService.enabled(this));
        AutoConnectService.nudgeReconnect(this);
    }
    private void refreshTrustSummary(){
        if(trustSummary==null)return;
        int count=new TrustedWindows(this).fingerprints().size();
        String connectedFp=DeskTouchRuntime.session(this).connectedFingerprint();
        boolean currentTrusted=connectedFp!=null &&
            new TrustedWindows(this).get(connectedFp)!=null;
        String base=count==0
            ?"Trusted computers saved: 0 (auto-reconnect disabled until approved)"
            :"Trusted computers saved: "+count;
        if(connectedFp!=null)
            base+=currentTrusted
                ?" • Current computer is trusted"
                :" • Current computer not trusted yet";
        trustSummary.setText(base);
        if(autoStartCheck!=null)autoStartCheck.setChecked(AutoConnectService.enabled(this));
    }
    private static void sendRemoteTextWithNewlines(DeskTouchSession target,String added){
        int start=0;
        for(int i=0;i<added.length();i++){
            if(added.charAt(i)=='\n'){
                if(i>start)target.text(added.substring(start,i));
                target.key(0x0D,true);target.key(0x0D,false);
                start=i+1;
            }
        }
        if(start<added.length())target.text(added.substring(start));
    }
    private void refreshPermissionState(){
        if(permissionState==null)return;
        permissionState.setText(PermissionContinuity.state(this) +
            "\nDeskTouch keyboard: " +
            (DeskTouchInputMethod.isSelected(this)
                ? "selected for focused-field typing."
                : "not selected (optional; use Enable and Select above)."));
    }
    private int dp(int size){return Math.round(getResources().getDisplayMetrics().density*size);}
    private void addCompactButton(LinearLayout parent,String label,Runnable action){
        Button b=new Button(this);
        b.setText(label);b.setAllCaps(false);b.setTextSize(12f);
        b.setMinWidth(0);b.setMinimumWidth(0);
        b.setPadding(dp(4),dp(6),dp(4),dp(6));
        b.setOnClickListener(v->action.run());
        LinearLayout.LayoutParams params=new LinearLayout.LayoutParams(
            0,-2,1f);
        params.setMargins(dp(2),0,dp(2),0);
        parent.addView(b,params);
    }
    private void addButton(LinearLayout parent,String label,Runnable action){
        Button b=new Button(this);
        b.setText(label);b.setAllCaps(false);
        b.setOnClickListener(v->action.run());
        parent.addView(b,new LinearLayout.LayoutParams(-1,-2));
    }
    private void refreshEnvironmentMembers(){
        if(environmentList==null)return;
        environmentList.removeAllViews();
        DeskTouchSession active=DeskTouchRuntime.session(this);
        List<DeskTouchSession.EnvironmentTarget> targets=active.environmentTargets();
        if(targets.isEmpty()){
            connectionSummary.setText(active.isConnected()
                ?"Waiting for authenticated environment membership..."
                :"No environment joined yet.");
            return;
        }
        connectionSummary.setText("One DeskTouch environment • "+
            targets.size()+" device(s) online");
        TextView heading=new TextView(this);
        heading.setText("Connected devices — same environment");
        heading.setTextColor(Color.WHITE);heading.setTextSize(18);
        environmentList.addView(heading);
        for(DeskTouchSession.EnvironmentTarget device:targets){
            String label=(device.id==active.selectedEnvironmentTarget()?"● ":"")
                +device.platform+" • "+device.name;
            if(device.id==active.ownEnvironmentId() || !device.supportsInput){
                TextView item=new TextView(this);
                item.setText(label+(device.id==active.ownEnvironmentId()
                    ?" • this phone":" • input permission required"));
                item.setTextColor(Color.LTGRAY);
                item.setPadding(0,dp(9),0,dp(9));
                environmentList.addView(item);
                continue;
            }
            addButton(environmentList,label,()->{
                active.selectEnvironmentTarget(device.id);
                active.claimControl();
            });
        }
    }
    private void scan(){
        if(scanning)return;
        scanning=true;
        status.setText("Finding a DeskTouch environment hub on your private LAN...");
        deviceList.removeAllViews();
        new Thread(()->{
            try{
                List<DesktopDiscovery.Device> list=DesktopDiscovery.scan();
                runOnUiThread(()->{
                    java.util.LinkedHashMap<String,List<DesktopDiscovery.Device>> groups=
                        DesktopDiscovery.windowsGroups(list);
                    java.util.Set<String> connected=new java.util.HashSet<>();
                    for(DesktopDiscovery.Device member:list)
                        if(!member.android&&DeskTouchRuntime.isConnected(member.fingerprint))
                            connected.add(member.fingerprint);
                    java.util.Set<String> approved=new java.util.HashSet<>();
                    try {approved.addAll(new TrustedWindows(this).fingerprints());}
                    catch(Exception ignored){}
                    if(groups.isEmpty())
                        status.setText("No DeskTouch environment found. Start DeskTouch on a Windows hub.");
                    else status.setText("Choose one environment. Connected members will "+
                        "appear automatically after joining the hub.");
                    for(List<DesktopDiscovery.Device> members:groups.values()){
                        DesktopDiscovery.Device gateway=DesktopDiscovery.chooseGateway(
                            members,connected,approved);
                        if(gateway==null)continue;
                        String names=String.join(" + ",members.stream()
                            .map(m->m.name).toArray(String[]::new));
                        String label="DeskTouch environment • "+names+
                            (DeskTouchRuntime.isConnected(gateway.fingerprint)
                                ?" • Joined":" • Join");
                        addButton(deviceList,label,()->{
                            if(DeskTouchRuntime.isConnected(gateway.fingerprint)){
                                session=DeskTouchRuntime.select(this,gateway.fingerprint);
                                refreshEnvironmentMembers();return;
                            }
                            if(DeskTouchRuntime.connectedCount()>0){
                                status.setText("Already joined a DeskTouch environment. "+
                                    "Leave it before switching hubs.");return;
                            }
                            if(!gateway.ready){
                                status.setText("Environment gateway is at capacity or unavailable.");
                                return;
                            }
                            new AlertDialog.Builder(this)
                                .setTitle("Join DeskTouch environment")
                                .setMessage("Connect once to "+gateway.name+
                                    ". Other Windows PCs and Android phones attached "+
                                    "to that hub will appear inside the same environment. "+
                                    "Each device joins through its own approved TLS "+
                                    "connection to the hub; this phone does not need to "+
                                    "pair separately with every member.")
                                .setNegativeButton("Cancel",null)
                                .setPositiveButton("Join environment",(dialog,which)->{
                                    android.graphics.Point size=PhoneScreenGeometry.read(this);
                                    AutoConnectService.setReconnect(this,true);
                                    AutoConnectService.resumePeer(this,gateway.fingerprint);
                                    session=DeskTouchRuntime.select(this,gateway.fingerprint);
                                    session.connect(gateway,size.x,size.y,true,null);
                                    status.setText("Connecting to shared environment through "+
                                        gateway.name+". Approve pairing on that Windows PC.");
                                }).show();
                        });
                    }
                });
            }catch(Exception e){
                runOnUiThread(()->status.setText("Environment discovery failed: "+e.getMessage()));
            }finally{scanning=false;}
        },"DeskTouch LAN environment discovery").start();
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
