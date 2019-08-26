using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

namespace EasyBoard
{
    /// <summary>
    /// Kerbal intention processor.
    /// </summary>
    public class Intention
    {
        public readonly KerbalEVA Kerbal;
        public readonly string KerbalName;

        private bool wantsToBoard;
        private bool wantsToGrab;
        private Part airlockPart;
        private KerbalSeat[] seats;

        // camera data
        private FlightCamera camera;
        public FlightCamera.Modes cameraMode;
        public Vector3 cameraPos;
        public Vector3 cameraPivotPos;

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
        /// The maximum distance to board the command seat.
        /// </summary>
        private const float SeatDistance = 2f;

        /// <summary>
        /// The original maximum distance to board the command seat.
        /// </summary>
        private const float OriginalSeatDistance = 4f;

        /// <summary>
        /// Initializes a new instance of the <see cref="Intention"/> class.
        /// </summary>
        /// <param name="kerbal">The kerbal.</param>
        public Intention(KerbalEVA kerbal)
        {
            Kerbal = kerbal;
            KerbalName = Kerbal.vessel.vesselName;
        }

        /// <summary>
        /// Processes the situation.
        /// </summary>
        /// <param name="isBoardKeyJustPressed">if set to <c>true</c> [is board key just pressed].</param>
        /// <returns></returns>
        public Result ProcessSituation(bool isBoardKeyJustPressed)
        {
            if (Kerbal != null)
            {
                if (wantsToBoard)
                {
                    Part newAirlockPart = this.GetKerbalAirlock(Kerbal);

                    if (airlockPart != newAirlockPart)
                    {
                        // Keep previous airlock to avoid multiple attemts to board it.
                        airlockPart = newAirlockPart;

                        if (newAirlockPart != null && /*Kerbal.vessel.state == Vessel.State.ACTIVE &&*/ !Kerbal.vessel.packed)
                        {
                            if (newAirlockPart.protoModuleCrew.Count < newAirlockPart.CrewCapacity)
                            {
                                // There is enough place for the kerbal,
                                // boarding should be successful. We can reset fields.
                                Reset();
                                SaveCameraDirection();
                            }

                            // Try to board. In case the cabin is full - game will display appropriate message.
                            Kerbal.BoardPart(newAirlockPart);

                            return wantsToBoard ? Result.None : Result.Boarded;
                        }
                    }

                    // Try board nearest seat when no airlock.
                    if (newAirlockPart == null)
                    {
                        KerbalSeat seat = GetNearestSeat(isBoardKeyJustPressed ? OriginalSeatDistance : SeatDistance);

                        if (seat != null)
                        {
                            //seat.BoardSeat();
                            if (seat.SeatBoundsAreClear(Kerbal.part))
                            {
                                SaveCameraDirection();

                                if (Kerbal.BoardSeat(seat))
                                {
                                    SetObjectField<Part>(seat.GetType(), seat, "occupant", Kerbal.part);
                                    ((PartModule)seat).Events["BoardSeat"].active = false;
                                }
                            }


                            // Check whether boarding seat was successful.
                            if (!((PartModule)seat).Events["BoardSeat"].active)
                            {
                                Reset();
                                return Result.Boarded;
                            }
                        }
                    }
                }

                if (wantsToGrab && !Kerbal.OnALadder)
                {
                    var ladderTriggers = GetObjectField<ICollection>(typeof(KerbalEVA), Kerbal, "currentLadderTriggers");

                    if (ladderTriggers != null && ladderTriggers.Count > 0)
                    {
                        foreach (var stateEvent in Kerbal.fsm.CurrentState.StateEvents)
                        {
                            if (stateEvent.name == "Ladder Grab Start")
                            {
                                wantsToGrab = false;
                                Kerbal.fsm.RunEvent(stateEvent);

                                return Result.Grabbed;
                            }
                        }
                    }
                }
            }

            return Result.None;
        }

        /// <summary>
        /// Switches the board intention.
        /// </summary>
        public void SwitchBoardIntention()
        {
            if (IsIntentionAllowed())
            {
                wantsToBoard = !wantsToBoard;

                var message = KerbalName + (wantsToBoard ? WantsToBoardMessage : HesitatingMessage);
                ScreenMessages.PostScreenMessage(message, 3f);
            }
        }

        /// <summary>
        /// Switches the grab intention.
        /// </summary>
        public void SwitchGrabIntention()
        {
            if (IsIntentionAllowed() && !Kerbal.OnALadder)
            {
                wantsToGrab = !wantsToGrab;

                var message = KerbalName + (wantsToGrab ? WantsToGrabMessage : HesitatingMessage);
                ScreenMessages.PostScreenMessage(message, 3f);
            }
        }

        /// <summary>
        /// Checks whether the kerbal is not busy and can board/grab some vessel.
        /// </summary>
        /// <returns></returns>
        private bool IsIntentionAllowed()
        {
            return Kerbal != null                                           // Kerbal exists.
                && !MapView.MapIsEnabled                                    // Player is not on Map View now.
                && !Kerbal.Animations.flagPlant.State.enabled               // Kerbal is not planting flag now.
                && EventSystem.current.currentSelectedGameObject == null;   // Player is not typing text in some UI text field.
        }

        /// <summary>
        /// Determines whether this intention is completed.
        /// </summary>
        public bool IsCompleted()
        {
            return (!wantsToBoard && !wantsToGrab) || Kerbal == null;
        }

        /// <summary>
        /// Saves the camera direction.
        /// </summary>
        public void SaveCameraDirection()
        {
            camera = FlightCamera.fetch;
            cameraMode = camera.mode;
            cameraPos = camera.GetCameraTransform().position;
            cameraPivotPos = camera.GetPivot().position;
        }

        /// <summary>
        /// Loads the camera direction that was before.
        /// </summary>
        public void LoadCameraDirection()
        {
            if (camera != null)
            {
                camera.mode = cameraMode;
                camera.GetPivot().position = cameraPivotPos;
                camera.SetCamCoordsFromPosition(cameraPos);
            }
        }

        /// <summary>
        /// Gets the nearest seat available for boarding.
        /// </summary>
        /// <param name="maxDistance">The maximum distance.</param>
        /// <returns>Nearest seat.</returns>
        private KerbalSeat GetNearestSeat(float maxDistance)
        {
            if (seats == null)
            {
                seats = GameObject.FindObjectsOfType<KerbalSeat>();
            }

            if (seats.Length > 0)
            {
                var nearSeats = new List<KerbalSeat>();

                // Get vessel seats available for boarding.
                foreach (var seat in seats)
                {
                    if (seat.Occupant == null &&
                        (seat.transform.position - Kerbal.vessel.transform.position).sqrMagnitude <= maxDistance)
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
                            (s1.transform.position - Kerbal.vessel.transform.position).sqrMagnitude.CompareTo(
                            (s2.transform.position - Kerbal.vessel.transform.position).sqrMagnitude));
                    }

                    return nearSeats[0];
                }
            }

            return null;
        }

        /// <summary>
        /// Resets this intention fields.
        /// </summary>
        private void Reset()
        {
            wantsToBoard = false;
            wantsToGrab = false;
            airlockPart = null;
            seats = null;
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
                return default;
            }
        }

        /// <summary>
        /// Sets the object field.
        /// This is temporary method and will be removed ASAP.
        /// </summary>
        /// <typeparam name="T">Field type.</typeparam>
        /// <param name="objectType">The object type.</param>
        /// <param name="instance">The object instance.</param>
        /// <param name="fieldName">Name of field to set.</param>
        /// <param name="newValue">The new value for field.</param>
        private void SetObjectField<T>(Type objectType, object instance, string fieldName, T newValue)
        {
            BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            FieldInfo field = objectType.GetField(fieldName, bindFlags);
            field.SetValue(instance, newValue);
        }

        /// <summary>
        /// Intention processing result.
        /// </summary>
        public enum Result
        {
            None,
            Grabbed,
            Boarded,
        }
    }
}
