using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using KSP.Localization;
using UniLinq;
using UnityEngine;
using UnityEngine.EventSystems;

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
        /// The status message duration.
        /// </summary>
        private const float MessageDuration = 3f;

        /// <summary>
        /// The maximum distance to board the command seat.
        /// </summary>
        private const float SeatDistance = 2f;

        /// <summary>
        /// The original maximum distance to board the command seat.
        /// </summary>
        private const float OriginalSeatDistance = 4f;

        /// <summary>
        /// The wants to board message.
        /// </summary>
        private const string WantsToBoardMessage = " wants to board";

        /// <summary>
        /// The wants to grab a ladder message.
        /// </summary>
        private const string WantsToGrabMessage = " wants to grab a ladder";

        /// <summary>
        /// The hesitating message.
        /// </summary>
        private const string HesitatingMessage = " is hesitating";

        /// <summary>
        /// The control lock identifier.
        /// </summary>
        private const string ControlLockId = "EasyBoard_ControlLock";
        #endregion

        #region Private_Fields
        /// <summary>
        /// Indicates whether crew wants to board.
        /// </summary>
        private bool WantsToBoard = false;

        /// <summary>
        /// Indicates whether crew wants to grab a ladder.
        /// </summary>
        private bool WantsToGrab = false;

        /// <summary>
        /// The name of current kerbal.
        /// </summary>
        private string KerbalName = string.Empty;

        /// <summary>
        /// The previously found airlock.
        /// </summary>
        private Part AirlockPart = null;

        /// <summary>
        /// Indicates whether to allow addon messages.
        /// </summary>
        private bool AllowMessages = true;

        /// <summary>
        /// The releasing control keys flag.
        /// </summary>
        private bool ReleasingKeys = false;

        /// <summary>
        /// The all loaded kerbal seats.
        /// </summary>
        private KerbalSeat[] Seats = null;

        /// <summary>
        /// The code to perform with next Unity frame.
        /// </summary>
        private Action DeferredUpdate = null;

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
            MapView.OnEnterMapView += AddonReset;
            GameEvents.onVesselChange.Add(data => AddonReset());
        }

        /// <summary>
        /// Update is called once per frame.
        /// </summary>
        public void Update()
        {
            if (DeferredUpdate != null)
            {
                DeferredUpdate();
                DeferredUpdate = null;
            }

            this.CheckVesselControl();

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

            if (FlightGlobals.ActiveVessel.isEVA)
            {
                string message = string.Empty;

                KerbalEVA kerbal = FlightGlobals.ActiveVessel.evaController;

                if (kerbal == null)
                {
                    return;
                }

                if (IsKeyUp(this.BoardKey))
                {
                    // Prevent addon on map view, or when kerbal is busy,
                    // or when player is typing text in some text field.
                    if (!this.CanKerbalStartToWant(kerbal))
                    {
                        return;
                    }

                    this.AllowMessages = true;
                    this.WantsToBoard = !this.WantsToBoard;

                    if (this.WantsToBoard)
                    {
                        this.Seats = null;
                        this.KerbalName = FlightGlobals.ActiveVessel.vesselName;
                    }

                    message = this.GetStatusMessage(this.WantsToBoard ? WantsToBoardMessage : HesitatingMessage);
                }

                if (IsKeyUp(this.GrabKey) && !kerbal.OnALadder)
                {
                    string pattern = "[" + this.GrabKey.ToString() + "]:";
                    bool canGrabNow = false;

                    ScreenMessages screenMessages = GameObject.FindObjectOfType<ScreenMessages>();
                    foreach (var activeMessage in screenMessages.ActiveMessages)
                    {
                        if (activeMessage.message.StartsWith(pattern, StringComparison.InvariantCultureIgnoreCase))
                        {
                            canGrabNow = true;
                            break;
                        }
                    }

                    if (!canGrabNow)
                    {
                        // Prevent addon on map view, or when kerbal is busy,
                        // or when player is typing text in some text field.
                        if (!this.CanKerbalStartToWant(kerbal))
                        {
                            return;
                        }

                        this.AllowMessages = true;
                        this.WantsToGrab = !this.WantsToGrab;

                        if (this.WantsToGrab)
                        {
                            this.KerbalName = FlightGlobals.ActiveVessel.vesselName;
                        }

                        message = this.GetStatusMessage(this.WantsToGrab ? WantsToGrabMessage : HesitatingMessage);
                    }
                }

                if (this.WantsToBoard)
                {
                    Part airlockPart = this.GetKerbalAirlock(kerbal);

                    if (this.AirlockPart != airlockPart)
                    {
                        // Keep previous airlock to avoid multiple attemts to board it.
                        this.AirlockPart = airlockPart;

                        if (airlockPart != null && kerbal.vessel.state == Vessel.State.ACTIVE && !kerbal.vessel.packed)
                        {
                            if (airlockPart.protoModuleCrew.Count < airlockPart.CrewCapacity)
                            {
                                // There is enough place for the kerbal,
                                // boarding should be successful. We can reset addon fields.
                                this.AllowMessages = false;
                                this.AddonReset();
                            }

                            // Try board.
                            this.LockVesselControl();
                            kerbal.BoardPart(airlockPart);
                            return;
                        }
                    }

                    // Try board nearest seat when no airlock.
                    if (airlockPart == null)
                    {
                        KerbalSeat seat = this.GetNearestSeat(kerbal,
                            IsKeyUp(this.BoardKey) ? OriginalSeatDistance : SeatDistance);

                        if (seat != null)
                        {
                            this.AllowMessages = false;
                            this.LockVesselControl();
                            seat.BoardSeat();

                            // Check whether boarding seat was successful.
                            if (((PartModule)seat).Events["BoardSeat"].active)
                            {
                                // Fail case.
                                this.AllowMessages = true;
                            }
                            else
                            {
                                // Success case.
                                this.AddonReset();
                                return;
                            }
                        }
                    }
                }

                if (this.WantsToGrab && !kerbal.OnALadder)
                {
                    var ladderTriggers = GetObjectField<ICollection>(typeof(KerbalEVA), kerbal, "currentLadderTriggers");

                    if (ladderTriggers != null && ladderTriggers.Count > 0)
                    {
                        foreach (var stateEvent in kerbal.fsm.CurrentState.StateEvents)
                        {
                            if (stateEvent.name == "Ladder Grab Start")
                            {
                                this.AllowMessages = false;
                                this.WantsToGrab = false;
                                this.LockVesselControl();
                                kerbal.fsm.RunEvent(stateEvent);
                                break;
                            }
                        }
                    }

                    //ScreenMessages screenMessages = GameObject.FindObjectOfType<ScreenMessages>();
                    //foreach (var activeMessage in screenMessages.ActiveMessages)
                    //{
                    //    if (activeMessage.message.EndsWith("]: Grab", StringComparison.InvariantCultureIgnoreCase))
                    //    //if (activeMessage.message == KerbalEVA.cacheAutoLOC_114130)
                    //    {
                    //        DoSearch(kerbal, "Grab", "kerbal");
                    //        DoSearch(kerbal, "grab", "kerbal");

                    //        foreach (var stateEvent in kerbal.fsm.CurrentState.StateEvents)
                    //        {
                    //            if (stateEvent.name == "Ladder Grab Start")
                    //            {
                    //                this.AllowMessages = false;
                    //                this.WantsToGrab = false;
                    //                this.LockVesselControl();
                    //                kerbal.fsm.RunEvent(stateEvent);
                    //                break;
                    //            }
                    //        }

                    //        break;
                    //    }
                    //}
                }

                this.DisplayMessage(message);
            }
        }

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
            return vessel != null && vessel.IsControllable || vessel.isCommandable;
        }

        /// <summary>
        /// Addon shutdown logic.
        /// </summary>
        public void OnDestroy()
        {
            GameEvents.onVesselChange.Remove(data => AddonReset());
            MapView.OnEnterMapView -= AddonReset;
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
        /// Checks whether the kerbal is not busy and can board any vessel.
        /// </summary>
        /// <param name="kerbal">The kerbal.</param>
        /// <returns>True when kerbal can start to want to board, otherwise false.</returns>
        private bool CanKerbalStartToWant(KerbalEVA kerbal)
        {
            return kerbal != null                                           // Kerbal exists.
                && !MapView.MapIsEnabled                                    // Player is not on Map View now.
                && !kerbal.Animations.flagPlant.State.enabled               // Kerbal is not planting flag now.
                && EventSystem.current.currentSelectedGameObject == null;   // Player is not typing text in some UI text field.
        }

        /// <summary>
        /// Gets the kerbal airlock.
        /// </summary>
        /// <param name="kerbal">The kerbal.</param>
        /// <returns>Airlock part.</returns>
        private Part GetKerbalAirlock(KerbalEVA kerbal)
        {
            // Have to use reflection until I found the correct approach to get kerbal current airlock.
            // If you read this and have an idea how to avoid reflection please
            // contact me on forum http://forum.kerbalspaceprogram.com/index.php?/profile/161502-dizor/
            return GetObjectField<Part>(typeof(KerbalEVA), kerbal, "currentAirlockPart");
        }

        /// <summary>
        /// Gets the nearest seat available for boarding.
        /// </summary>
        /// <param name="kerbal">The current kerbal.</param>
        /// <param name="maxDistance">The maximum distance.</param>
        /// <returns>Nearest seat.</returns>
        private KerbalSeat GetNearestSeat(KerbalEVA kerbal, float maxDistance)
        {
            if (this.Seats == null)
            {
                this.Seats = FindObjectsOfType<KerbalSeat>();
            }

            if (this.Seats.Length > 0)
            {
                var nearSeats = new List<KerbalSeat>();

                // Get vessel seats available for boarding.
                foreach (KerbalSeat seat in this.Seats)
                {
                    if (seat.Occupant == null &&
                        (seat.transform.position - kerbal.vessel.transform.position).sqrMagnitude <= maxDistance)
                    {
                        nearSeats.Add(seat);
                    }
                }

                if (nearSeats.Count > 0)
                {
                    // Get nearest seat.
                    if (nearSeats.Count > 1)
                    {
                        nearSeats.Sort((s1, s2) =>
                            (s1.transform.position - kerbal.vessel.transform.position).sqrMagnitude.CompareTo(
                            (s2.transform.position - kerbal.vessel.transform.position).sqrMagnitude));
                    }

                    return nearSeats[0];
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the kerbal status message.
        /// </summary>
        /// <returns>The kerbal status message.</returns>
        private string GetStatusMessage(string message)
        {
            if (!string.IsNullOrEmpty(this.KerbalName))
            {
                return this.KerbalName + message;
            }

            return string.Empty;
        }

        /// <summary>
        /// Resets the addon flags and conditions.
        /// </summary>
        private void AddonReset()
        {
            this.WantsToBoard = false;
            this.WantsToGrab = false;
            this.DisplayMessage(this.GetStatusMessage(HesitatingMessage));
            this.KerbalName = string.Empty;
            this.AirlockPart = null;
        }

        /// <summary>
        /// Displays the message.
        /// </summary>
        /// <param name="message">The message text.</param>
        private void DisplayMessage(string message)
        {
            if (this.AllowMessages && !string.IsNullOrEmpty(message))
            {
                ScreenMessages.PostScreenMessage(message, MessageDuration);
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

        /// <summary>
        /// Gets field of the object.
        /// This is temporary method and will be removed ASAP.
        /// </summary>
        /// <typeparam name="T">Object field type.</typeparam>
        /// <param name="type">The object type.</param>
        /// <param name="instance">The object instance.</param>
        /// <param name="fieldName">Name of field to get.</param>
        /// <returns>Field value.</returns>
        private T GetObjectField<T>(Type type, object instance, string fieldName)
        {
            try
            {
                BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.NonPublic;
                FieldInfo field = type.GetField(fieldName, bindFlags);

                return (T)field.GetValue(instance);
            }
            catch
            {
                return default(T);
            }
        }

        /// <summary>
        /// Sets the object field.
        /// This is temporary method and will be removed ASAP.
        /// </summary>
        /// <typeparam name="T">Object field type.</typeparam>
        /// <param name="type">The object type.</param>
        /// <param name="instance">The object instance.</param>
        /// <param name="fieldName">Name of field to set.</param>
        /// <param name="newValue">The new value for field.</param>
        //internal static void SetObjectField<T>(Type type, object instance, string fieldName, T newValue)
        //{
        //    BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        //    FieldInfo field = type.GetField(fieldName, bindFlags);
        //    field.SetValue(instance, newValue);
        //}
        #endregion
    }
}
