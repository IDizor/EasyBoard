using System;
using System.Collections.Generic;
using System.Reflection;
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
        /// The board key code.
        /// </summary>
        public const KeyCode BoardKey = KeyCode.B;

        /// <summary>
        /// The status message delay.
        /// </summary>
        private const float MessageDelay = 3f;

        /// <summary>
        /// The addon maximum distance to a seat to board.
        /// </summary>
        private const float SeatDistance = 2f;

        /// <summary>
        /// The original maximum distance to a seat to board.
        /// </summary>
        private const float OriginalSeatDistance = 4f;
        #endregion

        #region Private_Fields
        /// <summary>
        /// Indicates whether crew wants board.
        /// </summary>
        private bool WantsBoard = false;

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
        /// Addon update logic.
        /// </summary>
        public void Update()
        {
            if (FlightGlobals.ActiveVessel.isEVA)
            {
                string message = string.Empty;

                if (Input.GetKeyDown(BoardKey))
                {
                    if (MapView.MapIsEnabled || !this.CrewCanBoard(FlightGlobals.ActiveVessel.evaController))
                    {
                        return;
                    }

                    this.AllowMessages = true;
                    this.WantsBoard = !this.WantsBoard;

                    if (this.WantsBoard)
                    {
                        this.KerbalName = FlightGlobals.ActiveVessel.vesselName;
                    }

                    message = this.GetStatusMessage();
                }

                KerbalEVA kerbal = FlightGlobals.ActiveVessel.evaController;

                if (this.WantsBoard && kerbal != null)
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
                                this.AllowMessages = false;
                                this.AddonReset();
                            }

                            kerbal.BoardPart(airlockPart);
                        }
                    }

                    // Try board nearest seat when no airlock.
                    if (airlockPart == null)
                    {
                        KerbalSeat seat = this.GetNearestSeat(kerbal,
                            Input.GetKeyDown(BoardKey) ? OriginalSeatDistance : SeatDistance);

                        if (seat != null)
                        {
                            this.AllowMessages = false;
                            seat.BoardSeat();

                            if (((PartModule)seat).Events["BoardSeat"].active)
                            {
                                this.AllowMessages = true;
                            }
                            else
                            {
                                this.AddonReset();
                            }
                        }
                    }
                }

                this.DisplayMessage(message);
            }
        }

        /// <summary>
        /// Called on addon instance destroy.
        /// </summary>
        public void OnDestroy()
        {
            GameEvents.onVesselChange.Remove(data => AddonReset());
            MapView.OnEnterMapView -= AddonReset;
        }
        #endregion

        #region Private_Methods
        /// <summary>
        /// Check if crew can board.
        /// </summary>
        /// <param name="kerbal">The kerbal.</param>
        /// <returns>True when crew can board.</returns>
        private bool CrewCanBoard(KerbalEVA kerbal)
        {
            return kerbal != null
                && !kerbal.Animations.flagPlant.State.enabled
                && EventSystem.current.currentSelectedGameObject == null;
        }

        /// <summary>
        /// Gets the kerbal airlock.
        /// </summary>
        /// <param name="kerbal">The kerbal.</param>
        /// <returns>Airlock part.</returns>
        private Part GetKerbalAirlock(KerbalEVA kerbal)
        {
            // Have to use reflection until I found the correct approach to get kerbal current airlock.
            // If you read this and have any idea how to avoid reflection please
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
            KerbalSeat nearestSeat = null;
            Vessel nearestVessel = null;

            List<Vessel> vessels = new List<Vessel>();

            // Get loaded vessels.
            foreach (Vessel vessel in FlightGlobals.Vessels)
            {
                if (vessel.loaded && !vessel.packed && vessel != kerbal.vessel)
                {
                    vessels.Add(vessel);
                }
            }

            if (vessels.Count > 0)
            {
                // Get nearest vessel.
                if (vessels.Count > 1)
                {
                    vessels.Sort((v1, v2) =>
                        (v1.transform.position - kerbal.vessel.transform.position).sqrMagnitude.CompareTo(
                        (v2.transform.position - kerbal.vessel.transform.position).sqrMagnitude));
                }

                nearestVessel = vessels[0];

                List<KerbalSeat> seats = new List<KerbalSeat>();

                // Get vessel seats available for boarding.
                foreach (KerbalSeat seat in nearestVessel.FindPartModulesImplementing<KerbalSeat>())
                {
                    if (((PartModule)seat).Events["BoardSeat"].active &&
                        (seat.transform.position - kerbal.vessel.transform.position).sqrMagnitude <= maxDistance)
                    {
                        seats.Add(seat);
                    }
                }

                if (seats.Count > 0)
                {
                    // Get nearest seat.
                    if (seats.Count > 1)
                    {
                        seats.Sort((s1, s2) =>
                            (s1.transform.position - kerbal.vessel.transform.position).sqrMagnitude.CompareTo(
                            (s2.transform.position - kerbal.vessel.transform.position).sqrMagnitude));
                    }

                    nearestSeat = seats[0];
                }
            }

            return nearestSeat;
        }

        /// <summary>
        /// Gets the kerbal status message.
        /// </summary>
        /// <returns>The kerbal status message.</returns>
        private string GetStatusMessage()
        {
            if (!string.IsNullOrEmpty(this.KerbalName))
            {
                return this.KerbalName + (this.WantsBoard ? " wants board" : " hesitating");
            }

            return string.Empty;
        }

        /// <summary>
        /// Resets the addon flags and conditions.
        /// </summary>
        private void AddonReset()
        {
            this.WantsBoard = false;
            this.DisplayMessage(this.GetStatusMessage());
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
                ScreenMessages.PostScreenMessage(message, MessageDelay);
            }
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
        internal static T GetObjectField<T>(Type type, object instance, string fieldName)
        {
            BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            FieldInfo field = type.GetField(fieldName, bindFlags);
            return (T)field.GetValue(instance);
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
