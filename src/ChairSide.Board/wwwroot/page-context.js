export function derivePageContext({
  view,
  bodyRoomNumber,
  search,
  roomToken
}) {
  const query = new URLSearchParams(search);
  const requestedRoom = bodyRoomNumber || query.get("roomId") || query.get("room") || "1";
  const parsedRoomNumber = Number(requestedRoom);

  return {
    view,
    isMaster: view === "master",
    isDoctor: view === "doctor",
    isRoom: view === "room",
    isReports: view === "reports",
    isWorkshop: view === "workshop",
    roomNumber: Number.isInteger(parsedRoomNumber) ? parsedRoomNumber : 0,
    roomToken: view === "room" ? roomToken : "",
    doctorId: query.get("doctorId") || query.get("doctor")
  };
}

const view = document.body.dataset.view;
const bodyRoomNumber = document.body.dataset.roomNumber;
const search = location.search;
const initialContext = derivePageContext({
  view,
  bodyRoomNumber,
  search,
  roomToken: ""
});
const roomToken = initialContext.isRoom
  ? document.querySelector("meta[name='chairside-room-token']")?.content
    || sessionStorage.getItem(`chairside-room-token-${initialContext.roomNumber}`)
    || ""
  : "";

export const pageContext = {
  ...initialContext,
  roomToken
};
