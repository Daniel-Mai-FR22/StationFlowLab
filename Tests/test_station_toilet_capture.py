import copy
import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'BuildRequests'))
from validate_station_toilet_capture import audit_events


def point(x=0, z=0):
    return dict(x=x, y=0, z=z)


class ToiletCaptureTests(unittest.TestCase):
    def setUp(self):
        self.control = dict(capacity=2, queueCapacity=3, queueSlots=[point(0, 0), point(0, -1), point(0, -2)],
                            usePoints=[point(2, -1), point(2, 1)], exitPoint=point(0, 3),
                            arrivalTolerance=.22, minimumQueueSeparation=.7, minimumUseSeconds=120, maximumUseSeconds=240)
        def event(kind, ident, time, count, uses, position, **extra):
            return dict(kind=kind, agentId=ident, time=time, toiletQueueCount=count,
                        toiletUseCount=uses, toiletCapacity=2, position=position, **extra)
        def joined(ident, time, uses):
            return event('toilet_queue_joined', ident, time, 1, uses, point(),
                         queueSlotIndex=0, queueArrivalPosition=point(), queuePredecessorJourneyId='')
        def reserved(ident, time, uses, index):
            return event('toilet_use_reserved', ident, time, 0, uses, point(), toiletUseSlotIndex=index)
        def started(ident, time, uses, index, duration):
            return event('toilet_use_started', ident, time, 0, uses, self.control['usePoints'][index],
                         toiletUseSlotIndex=index, toiletUseDurationSeconds=duration,
                         toiletUseStartedAtSeconds=time, outcome='WCUsing')
        self.events = [joined('A', 0, 0), reserved('A', .1, 1, 0), started('A', 2, 1, 0, 120),
                       joined('B', 3, 1), reserved('B', 3.1, 2, 1), started('B', 5, 2, 1, 150),
                       joined('C', 6, 2), event('toilet_use_completed', 'A', 122, 1, 2, point(2, -1)),
                       event('toilet_use_released', 'A', 123, 1, 1, point(0, 3)),
                       dict(kind='activity_completed', agentId='A', time=124, position=point(0, 3), detail='Toilet; sampled dwell=120'),
                       reserved('C', 125, 2, 0), started('C', 127, 2, 0, 180)]

    def reject(self):
        self.assertFalse(audit_events(self.control, self.events)['success'])

    def test_valid_two_places_and_pending_window_end(self):
        report = audit_events(self.control, self.events)
        self.assertTrue(report['success'], report['errors'])
        self.assertEqual(report['usePeak'], 2)
        self.assertEqual(report['sampledDurationsSeconds'], [120, 150, 180])

    def test_remote_registration(self):
        self.events[0]['position'] = point(1)
        self.reject()

    def test_use_starts_before_arrival(self):
        self.events[2]['position'] = point()
        self.reject()

    def test_short_visit(self):
        self.events[2]['toiletUseDurationSeconds'] = 17
        self.reject()

    def test_early_completion(self):
        self.events[7]['time'] = 100
        self.reject()

    def test_capacity_release_before_body_clearance(self):
        self.events[8]['position'] = point(2, -1)
        self.reject()

    def test_duplicate_use_place(self):
        self.events[4]['toiletUseSlotIndex'] = 0
        self.reject()

    def test_fifo_bypass(self):
        self.events[10]['agentId'] = 'D'
        self.reject()

    def test_completion_before_reaching_exit(self):
        self.events[9]['position'] = point(2, -1)
        self.reject()

    def test_capacity_telemetry_lies(self):
        self.events[5]['toiletUseCount'] = 1
        self.reject()


if __name__ == '__main__':
    unittest.main()
