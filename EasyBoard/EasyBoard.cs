using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using KSP.Localization;
using UniLinq;
using UnityEngine;

namespace EasyBoard
{
    /// <summary>
    /// Implements functionality for EasyBoard KSP add-on.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class EasyBoard : MonoBehaviour
    {
        #region Private_Constants
        /// <summary>
        /// The control lock identifier.
        /// </summary>
        private const string ControlLockId = "EasyBoard_ControlLock";
        #endregion

        #region Private_Fields        
        /// <summary>
        /// The intentions.
        /// </summary>
        private List<Intention> intentions;

        /// <summary>
        /// The releasing control keys flag.
        /// </summary>
        private bool ReleasingKeys;

        /// <summary>
        /// The code to perform with next Unity frame.
        /// </summary>
        private Action DeferredUpdate;

        /// <summary>
        /// Gets the boarding key code from game configuration.
        /// </summary>
        private KeyCode BoardKey
        {
            get
            {
                return GameSettings.EVA_Board.primary.code;
            }
        }

        /// <summary>
        /// Gets the grab key code from game configuration.
        /// </summary>
        private KeyCode GrabKey
        {
            get
            {
                return GameSettings.EVA_Use.primary.code;
            }
        }
        #endregion

        #region Public_Methods
        /// <summary>
        /// Addon start logic.
        /// </summary>
        public void Start()
        {
            intentions = new List<Intention>();
        }

        /// <summary>
        /// Update is called once per Unity frame.
        /// </summary>
        public void Update()
        {
            if (DeferredUpdate != null)
            {
                DeferredUpdate();
                DeferredUpdate = null;
            }

            this.CheckVesselControl();

            // switching vessel
            if (!GameSettings.MODIFIER_KEY.GetKey(false))
            {
                if (GameSettings.FOCUS_NEXT_VESSEL.GetKeyDown(false))
                {
                    SwitchToControllableVessel(true);
                }

                if (GameSettings.FOCUS_PREV_VESSEL.GetKeyDown(false))
                {
                    SwitchToControllableVessel(false);
                }
            }

            var isBoardKey = false;
            var isGrabKey = false;

            KerbalEVA kerbal = FlightGlobals.ActiveVessel.evaController;

            // boarding and grabbing
            if (FlightGlobals.ActiveVessel.isEVA && kerbal != null)
            {
                isBoardKey = IsKeyUp(this.BoardKey);
                isGrabKey = IsKeyUp(this.GrabKey);

                if (isBoardKey)
                {
                    GetOrCreateIntention(kerbal).SwitchBoardIntention();
                }

                if (isGrabKey)
                {
                    // deal with available grab/climb action at the moment
                    string pattern = "[" + this.GrabKey.ToString() + "]:";
                    bool grabOrClimbIsAlreadyAvailable = false;
                    ScreenMessages screenMessages = GameObject.FindObjectOfType<ScreenMessages>();

                    foreach (var activeMessage in screenMessages.ActiveMessages)
                    {
                        if (activeMessage.message.StartsWith(pattern, StringComparison.InvariantCultureIgnoreCase))
                        {
                            grabOrClimbIsAlreadyAvailable = true;
                            break;
                        }
                    }

                    if (!grabOrClimbIsAlreadyAvailable)
                    {
                        GetOrCreateIntention(kerbal).SwitchGrabIntention();
                    }
                }
            }

            // process situation for all intentions
            foreach (var intention in intentions)
            {
                var intentionResult = intention.ProcessSituation(isBoardKey);

                if (intentionResult != Intention.Result.None)
                {
                    // if current kerbal is intention owner
                    if (kerbal == intention.Kerbal)
                    {
                        if (intentionResult == Intention.Result.Boarded)
                        {
                            intention.LoadCameraDirection();
                            LockVesselControl();
                        }
                    }
                    // outer kerbal intention processed
                    else
                    {
                        // reactivate current vessel to refresh crew list
                        FlightGlobals.ActiveVessel.MakeActive();
                        GameEvents.onVesselChange.Fire(FlightGlobals.ActiveVessel);
                        intention.LoadCameraDirection();
                    }
                }
            }

            // remove completed intentions
            if (intentions.Count > 0)
            {
                intentions.RemoveAll(i => i.IsCompleted());
            }
        }

        /// <summary>
        /// Gets or creates intention from list.
        /// </summary>
        /// <param name="kerbal">The kerbal.</param>
        /// <returns></returns>
        private Intention GetOrCreateIntention(KerbalEVA kerbal)
        {
            var intention = intentions.FirstOrDefault(i => i.Kerbal == kerbal);

            if (intention == null)
            {
                intention = new Intention(kerbal);
                intentions.Add(intention);
            }

            return intention;
        }

        /// <summary>
        /// Switches to controllable vessel.
        /// </summary>
        /// <param name="switchForward">if set to <c>true</c> [switch forward].</param>
        private void SwitchToControllableVessel(bool switchForward)
        {
            if (FlightGlobals.ActiveVessel != null && !IsVesselControllable(FlightGlobals.ActiveVessel))
            {
                var go = switchForward ? 1 : -1;
                var loadedVessels = FlightGlobals.VesselsLoaded;
                var controllableVesselsCount = loadedVessels
                    .Where(v => IsVesselControllable(v))
                    .Count();

                if (loadedVessels.Count > 1 && controllableVesselsCount > 0)
                {
                    var activeVesselIndex = loadedVessels.IndexOf(FlightGlobals.ActiveVessel);
                    int i = activeVesselIndex;

                    do
                    {
                        i = (i + go + loadedVessels.Count) % loadedVessels.Count;

                        if (i != activeVesselIndex && IsVesselControllable(loadedVessels[i]))
                        {
                            if (controllableVesselsCount == 1)
                            {
                                // when contollable vessel is only one we have to switch back to it with next frame,
                                // otherwize crew faces in bottom right corner disappear.
                                DeferredUpdate = () => {
                                    FlightGlobals.ForceSetActiveVessel(loadedVessels[i]);
                                    FlightInputHandler.ResumeVesselCtrlState(loadedVessels[i]);
                                    ScreenMessages.PostScreenMessage(Localizer.Format("#autoLOC_137597"), 5f, ScreenMessageStyle.UPPER_CENTER);
                                };
                            }
                            else
                            {
                                FlightGlobals.ForceSetActiveVessel(loadedVessels[i]);
                                FlightInputHandler.ResumeVesselCtrlState(loadedVessels[i]);
                            }

                            break;
                        }
                    }
                    while (i != activeVesselIndex);
                }
            }
        }

        /// <summary>
        /// Checks whether vessel is controllable or commandable.
        /// </summary>
        /// <param name="vessel"></param>
        /// <returns></returns>
        public bool IsVesselControllable(Vessel vessel)
        {
            return vessel != null && (vessel.IsControllable || vessel.isCommandable);
        }
        #endregion

        #region Private_Methods        
        /// <summary>
        /// Does the object deep search.
        /// </summary>
        /// <param name="obj">The object.</param>
        /// <param name="term">The term.</param>
        /// <param name="path">The path.</param>
        private void DoObjectDeepSearch(object obj, string term, string path = "")
        {
            if (path.Split(new char[] { '.' }).Length > 10)
            {
                return;
            }

            Type type = obj.GetType();

            BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public;
            FieldInfo[] fields = type.GetFields(bindFlags);

            foreach (var field in fields)
            {
                var value = field.GetValue(obj);

                if (value != null)
                {
                    if (value.ToString().Contains(term))
                    {
                        // found
                    }
                    else if (value is string)
                    {
                        // skip
                    }
                    else if (typeof(ICollection).IsAssignableFrom(field.FieldType))
                    {
                        var elements = value as ICollection;

                        for (var i = 0; i < elements.Count; i++)
                        {
                            DoObjectDeepSearch(elements.OfType<object>().ElementAt(i), term, path + "." + field.Name + "[" + i.ToString() + "]");
                        }
                    }
                    else if (field.FieldType.IsClass)
                    {
                        DoObjectDeepSearch(value, term, path + "." + field.Name);
                    }
                }
            }
        }
              
        /// <summary>
        /// Checks the control keys to prevent unwanted control of just boarded vessel.
        /// </summary>
        private void CheckVesselControl()
        {
            if (this.ReleasingKeys &&
                !Input.GetKey(GameSettings.PITCH_UP.primary.code) && !Input.GetKey(GameSettings.PITCH_UP.secondary.code) &&
                !Input.GetKey(GameSettings.PITCH_DOWN.primary.code) && !Input.GetKey(GameSettings.PITCH_DOWN.secondary.code) &&
                !Input.GetKey(GameSettings.YAW_LEFT.primary.code) && !Input.GetKey(GameSettings.YAW_LEFT.secondary.code) &&
                !Input.GetKey(GameSettings.YAW_RIGHT.primary.code) && !Input.GetKey(GameSettings.YAW_RIGHT.secondary.code) &&
                !Input.GetKey(GameSettings.THROTTLE_UP.primary.code) && !Input.GetKey(GameSettings.THROTTLE_UP.secondary.code) &&
                !Input.GetKey(GameSettings.LAUNCH_STAGES.primary.code) && !Input.GetKey(GameSettings.LAUNCH_STAGES.secondary.code))
            {
                this.UnlockVesselControl();
            }
        }

        /// <summary>
        /// Locks the vessel control.
        /// </summary>
        private void LockVesselControl()
        {
            if (!this.ReleasingKeys)
            {
                this.ReleasingKeys = true;
                InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, ControlLockId);
            }
        }

        /// <summary>
        /// Unlocks the vessel control.
        /// </summary>
        private void UnlockVesselControl()
        {
            if (this.ReleasingKeys)
            {
                this.ReleasingKeys = false;
                InputLockManager.RemoveControlLock(ControlLockId);
            }
        }

        /// <summary>
        /// Determines whether specified key is released at the moment.
        /// </summary>
        /// <param name="keyCode">The key code.</param>
        private bool IsKeyUp(KeyCode keyCode)
        {
            return Input.GetKeyUp(keyCode)
                && !Input.GetKey(KeyCode.LeftControl)
                && !Input.GetKey(KeyCode.RightControl)
                && !Input.GetKey(KeyCode.LeftShift)
                && !Input.GetKey(KeyCode.RightShift)
                && !Input.GetKey(KeyCode.LeftAlt)
                && !Input.GetKey(KeyCode.RightAlt)
                && !Input.GetKey(KeyCode.CapsLock)
                && !Input.GetKey(KeyCode.Space)
                && !Input.GetKey(KeyCode.Tab)
                && !GameSettings.MODIFIER_KEY.GetKey();
        }
        #endregion
    }
}
