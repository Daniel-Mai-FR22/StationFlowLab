import unittest
from test_station_day_capture import fixture, validate


class DailyToiletSchemaTests(unittest.TestCase):
    def control(self, data):
        zero = dict(x=0, y=0, z=0)
        data[0]['toiletControls'] = [dict(poiId='wc', capacity=2, queueCapacity=1,
                                        queueSlots=[zero], usePoints=[zero, dict(x=1, y=0, z=0)],
                                        exitPoint=dict(x=0, y=0, z=1), arrivalTolerance=.22,
                                        minimumQueueSeparation=.7, minimumUseSeconds=120, maximumUseSeconds=240)]

    def test_new_wc_state_requires_control(self):
        data = fixture()
        data[2][0].update(activity='Toilet', execution='WCUsing')
        self.assertTrue(any('WC execution requires' in e for e in validate(data)['errors']))

    def test_new_wc_state_requires_toilet_activity(self):
        data = fixture(); self.control(data)
        data[2][0]['execution'] = 'WCQueue'
        self.assertTrue(any('WC execution requires' in e for e in validate(data)['errors']))

    def test_wc_control_schema_recognizes_explicit_state(self):
        data = fixture(); self.control(data)
        data[2][0].update(activity='Toilet', execution='WCApproach')
        result = validate(data)
        self.assertTrue(result['success'], result['errors'])

    def test_unknown_wc_state_remains_invalid(self):
        data = fixture(); self.control(data)
        data[2][0].update(activity='Toilet', execution='WCTeleport')
        self.assertTrue(any('unknown execution' in e for e in validate(data)['errors']))

    def test_wc_event_requires_saved_control(self):
        data = fixture()
        data[3].insert(2, dict(time=0, kind='toilet_queue_joined', agentId='a', serviceId='trip', representedPassengers=10))
        self.assertTrue(any('WC event without' in e for e in validate(data)['errors']))

    def test_wc_event_after_terminal_is_invalid(self):
        data = fixture(); self.control(data)
        data[3].insert(-1, dict(time=1.4, kind='toilet_queue_joined', agentId='b', serviceId='trip', representedPassengers=20))
        self.assertTrue(any('WC event outside' in e for e in validate(data)['errors']))


if __name__ == '__main__':
    unittest.main()
